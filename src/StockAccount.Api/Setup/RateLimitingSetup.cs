using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using StockAccount.Api.ErrorHandling;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Security;

namespace StockAccount.Api.Setup;

/// <summary>
/// ASP.NET Core rate limiting: a global sliding-window limiter partitioned per authenticated user id (else client IP)
/// for every endpoint except health checks, plus a much stricter per-IP policy for login/refresh. Rejections are 429
/// application/problem+json with code RATE_LIMITED and a Retry-After header.
/// </summary>
public static class RateLimitingSetup
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .Validate(o => !o.Enabled || (o.Global.PermitLimit > 0 && o.Global.WindowSeconds > 0 && o.Auth.PermitLimit > 0 && o.Auth.WindowSeconds > 0),
                "RateLimiting limits must be >= 1")
            .ValidateOnStart();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = OnRejectedAsync;

            if (!options.Enabled)
            {
                o.AddPolicy(RateLimitingOptions.AuthPolicy, _ => RateLimitPartition.GetNoLimiter("off"));
                return;
            }

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                IsExempt(http)
                    ? RateLimitPartition.GetNoLimiter("exempt")
                    : RateLimitPartition.GetSlidingWindowLimiter(PartitionKey(http), _ => Sliding(options.Global)));

            o.AddPolicy(RateLimitingOptions.AuthPolicy, http =>
                RateLimitPartition.GetSlidingWindowLimiter("auth:" + ClientIp(http), _ => Sliding(options.Auth)));
        });

        return services;
    }

    /// <summary>"u:{sub}" for authenticated callers, otherwise "ip:{client ip}" (after forwarded-header processing).</summary>
    public static string PartitionKey(HttpContext http)
    {
        var sub = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirst(AppClaimTypes.Subject)?.Value : null;
        return sub is not null ? "u:" + sub : "ip:" + ClientIp(http);
    }

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool IsExempt(HttpContext http) => http.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

    private static SlidingWindowRateLimiterOptions Sliding(RateLimitRule rule) => new()
    {
        PermitLimit = rule.PermitLimit,
        Window = TimeSpan.FromSeconds(rule.WindowSeconds),
        SegmentsPerWindow = Math.Clamp(rule.WindowSeconds, 1, 6),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitingOptions>>().Value;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) && ra > TimeSpan.Zero
            ? ra
            : TimeSpan.FromSeconds(Math.Max(1, Math.Min(options.Global.WindowSeconds, options.Auth.WindowSeconds)));
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);

        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting")
            .LogWarning("Rate limit exceeded for {Partition} on {Method} {Path}", PartitionKey(http), http.Request.Method, http.Request.Path);

        await ProblemWriter.WriteAsync(
            http,
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.RateLimited,
            $"Çok fazla istek gönderildi. Lütfen {seconds} saniye sonra tekrar deneyin.");
    }
}
