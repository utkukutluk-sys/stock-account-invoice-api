using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StockAccount.Infrastructure.Caching;

namespace StockAccount.Api.Setup;

public static class HealthSetup
{
    /// <summary>
    /// /health: SQL Server (Unhealthy → 503) and Redis. Redis is "Degraded" (still 200) when the in-memory fallback is in
    /// use or Redis stops answering, because cache failures never fail requests (CONTRACT §5.10, §6).
    /// </summary>
    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var builder = services.AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Default")!, name: "sql", tags: ["db", ReadyTag]);

        var cacheState = services
            .Where(d => d.ServiceType == typeof(CacheProviderState))
            .Select(d => d.ImplementationInstance)
            .OfType<CacheProviderState>()
            .FirstOrDefault();

        if (cacheState is { UsingRedis: true, Multiplexer: { } mux })
        {
            builder.AddRedis(_ => mux, name: "redis", failureStatus: HealthStatus.Degraded, tags: ["cache", ReadyTag]);
        }
        else
        {
            var description = cacheState?.Describe() ?? "cache not configured";
            builder.AddCheck("redis", () => HealthCheckResult.Degraded($"Redis not in use: {description}"), tags: ["cache", ReadyTag]);
        }

        return services;
    }

    public const string ReadyTag = "ready";

    /// <summary>
    /// <c>/health/live</c>: process only (no dependencies) — for container/orchestrator liveness.
    /// <c>/health/ready</c>: SQL (Unhealthy → 503) + Redis (Degraded → still 200, the API works without it).
    /// <c>/health</c>: alias of ready (CONTRACT §5.10). Not rate limited. Descriptions only in Development.
    /// </summary>
    public static void MapApiHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", Options(_ => false)).DisableRateLimiting();
        app.MapHealthChecks("/health/ready", Options(c => c.Tags.Contains(ReadyTag))).DisableRateLimiting();
        app.MapHealthChecks("/health", Options(c => c.Tags.Contains(ReadyTag))).DisableRateLimiting();
    }

    private static HealthCheckOptions Options(Func<HealthCheckRegistration, bool> predicate) => new()
    {
        Predicate = predicate,
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
        },
        ResponseWriter = WriteAsync,
    };

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var showDetails = context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";
        var body = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    description = showDetails ? e.Value.Description : null,
                    durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
