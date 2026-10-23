using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using StockAccount.Api.ErrorHandling;
using StockAccount.Api.Filters;
using StockAccount.Api.Json;
using StockAccount.Api.Middleware;
using StockAccount.Api.Security;
using StockAccount.Api.Setup;
using StockAccount.Application;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Security;
using Microsoft.EntityFrameworkCore;
using StockAccount.Infrastructure.Persistence;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Infrastructure;
using StockAccount.Infrastructure.Caching;
using StockAccount.Infrastructure.Startup;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Fail fast on missing/invalid configuration before anything depends on it (production readiness §2).
    // "--migrate-only": privileged one-shot step (compose service db-migrate): migrate + DB options + app login, then exit.
    var migrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
    var configCheck = ConfigurationValidator.Validate(builder.Configuration, builder.Environment, migrateOnly);
    foreach (var warning in configCheck.Warnings)
    {
        Log.Warning("CONFIGURATION WARNING: {Warning}", warning);
    }

    if (!configCheck.IsValid)
    {
        Log.Fatal("Invalid configuration, the API will not start: {Errors}", string.Join("; ", configCheck.Errors));
        return 2;
    }

    builder.WebHost.ConfigureKestrel(o => o.AddServerHeader = false);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Layers
    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
    // Schema guard + migrate + seed run in IHostedLifecycleService.StartingAsync, before Kestrel listens.
    if (!migrateOnly)
    {
        builder.Services.AddHostedService<DatabaseStartupService>();
    }
    var commandTimeout = builder.Configuration.GetValue("Database:CommandTimeoutSeconds", 30);
    builder.Services.ConfigureDbContext<AppDbContext>(o => o.UseSqlServer(sql => sql.CommandTimeout(commandTimeout)));
    builder.Services.AddOptions<JwtOptions>()
        .Validate(o => !string.IsNullOrWhiteSpace(o.Key) && System.Text.Encoding.UTF8.GetByteCount(o.Key) >= 32, "Jwt:Key must be at least 32 bytes")
        .ValidateOnStart();
    builder.Services.AddOptions<CacheOptions>()
        .Validate(o => o.EntityTtlMinutes > 0 && o.ListTtlMinutes > 0 && o.CircuitBreakSeconds > 0, "Cache TTLs must be >= 1")
        .ValidateOnStart();
    builder.Services.AddApiAuthentication(builder.Configuration);

    // MVC + JSON (camelCase, enums as strings, UTC DateTimes with Z)
    builder.Services
        .AddControllers(o =>
        {
            o.Filters.Add<ValidationFilter>();
            o.Filters.Add(new ProblemDetailsContentTypeFilter()); // instance so its IOrderedFilter.Order (after [Produces]) is used
            o.Conventions.Add(new SuccessResponseTypeConvention());
        })
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
        });
    builder.Services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ModelStateProblemFactory.Create);

    builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx => ProblemWriter.Enrich(ctx.HttpContext, ctx.ProblemDetails));
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // Only the configured reverse proxies (nginx / Nitro on the compose network) may set X-Forwarded-For/Proto; with an
    // empty list only loopback is trusted. The client IP drives the per-IP rate limits.
    var forwarded = builder.Configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.ForwardLimit = Math.Max(1, forwarded.ForwardLimit);
        foreach (var network in forwarded.KnownNetworks)
        {
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }

        foreach (var proxy in forwarded.KnownProxies)
        {
            o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        }
    });
    builder.Services.AddApiRateLimiting(builder.Configuration);
    builder.Services.AddHsts(o =>
    {
        o.MaxAge = TimeSpan.FromDays(365);
        o.IncludeSubDomains = true;
    });

    // CORS only for dev tooling (browsers use the same-origin /api proxy, CONTRACT §8.1).
    var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithExposedHeaders(CorrelationIdMiddleware.HeaderName, "X-Cache", "ETag", "Location")));

    builder.Services.AddApiSwagger();
    builder.Services.AddApiHealthChecks(builder.Configuration);

    var app = builder.Build();

    if (migrateOnly)
    {
        await DatabaseInitializer.MigrateOnlyAsync(app.Services, app.Configuration, app.Logger, requireAdmin: app.Environment.IsProduction());
        app.Logger.LogInformation("Migration step completed");
        return 0;
    }

    var cacheState = app.Services.GetRequiredService<CacheProviderState>();
    if (cacheState is { Enabled: true, UsingRedis: false })
    {
        app.Logger.LogWarning("Redis cache not used, falling back to {CacheProvider}", cacheState.Describe());
    }
    else
    {
        app.Logger.LogInformation("Cache provider: {CacheProvider}", cacheState.Describe());
    }


    app.UseForwardedHeaders();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    if (app.Environment.IsProduction())
    {
        app.UseHsts(); // only emitted on HTTPS requests (TLS terminates at the reverse proxy)
    }

    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(o =>
    {
        o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
        o.EnrichDiagnosticContext = (diagnostics, http) =>
        {
            // Runs when the request completes, i.e. after authentication: user and tenant are known here (BUG-5).
            diagnostics.Set("UserId", http.User.FindFirst("sub")?.Value);
            diagnostics.Set("CompanyId", UserLogContextMiddleware.ResolveCompanyId(http));
            diagnostics.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());
            diagnostics.Set("CorrelationId", CorrelationIdMiddleware.Get(http));
        };
    });
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    var swaggerEnabled = app.Configuration.GetSection(SwaggerSettings.SectionName).Get<SwaggerSettings>()?.Enabled ?? !app.Environment.IsProduction();
    if (swaggerEnabled)
    {
        app.UseSwagger();
        app.UseSwaggerUI(o =>
        {
            o.SwaggerEndpoint("/swagger/v1/swagger.json", "StockAccount API v1");
            o.RoutePrefix = "swagger";
            o.DocumentTitle = "StockAccount API";
        });
    }

    app.UseCors();
    app.UseAuthentication();
    app.UseRateLimiter(); // after authentication: partitions by user id when a valid token is present
    app.UseAuthorization();
    app.UseMiddleware<UserLogContextMiddleware>();
    app.UseMiddleware<ETagMiddleware>();

    app.MapControllers();
    app.MapApiHealthChecks();
    if (swaggerEnabled)
    {
        app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
    }

    await app.RunAsync();
    return 0;
}
catch (Microsoft.Extensions.Options.OptionsValidationException ex)
{
    Log.Fatal("Invalid configuration, the API will not start: {Errors}", string.Join("; ", ex.Failures));
    return 2;
}
catch (BootstrapConfigurationException ex)
{
    Log.Fatal("Admin bootstrap failed: {Error}", ex.Message);
    return 4;
}
catch (DatabaseSchemaMismatchException ex)
{
    Log.Fatal("{Error}", ex.Message);
    return 3;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker for WebApplicationFactory (integration tests).</summary>
public partial class Program;
