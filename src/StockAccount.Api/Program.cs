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

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // Layers
    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
    builder.Services.AddApiAuthentication(builder.Configuration);

    // MVC + JSON (camelCase, enums as strings, UTC DateTimes with Z)
    builder.Services
        .AddControllers(o =>
        {
            o.Filters.Add<ValidationFilter>();
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

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // The API sits behind the UI proxies (nginx / Nitro) on a private network; trust their forwarded headers.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
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

    var cacheState = app.Services.GetRequiredService<CacheProviderState>();
    if (cacheState is { Enabled: true, UsingRedis: false })
    {
        app.Logger.LogWarning("Redis cache not used, falling back to {CacheProvider}", cacheState.Describe());
    }
    else
    {
        app.Logger.LogInformation("Cache provider: {CacheProvider}", cacheState.Describe());
    }

    await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration, app.Logger, app.Lifetime.ApplicationStopping);

    app.UseForwardedHeaders();
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

    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "StockAccount API v1");
        o.RoutePrefix = "swagger";
        o.DocumentTitle = "StockAccount API";
    });

    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<UserLogContextMiddleware>();
    app.UseMiddleware<ETagMiddleware>();

    app.MapControllers();
    app.MapApiHealthChecks();
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API host terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker for WebApplicationFactory (integration tests).</summary>
public partial class Program;
