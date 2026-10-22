using System.Net;
using System.Text;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Reconciliation;

namespace StockAccount.Api.Setup;

/// <summary>
/// Fail-fast configuration check, run before any service is registered. Errors stop the process (exit code 2) with one log
/// line that names the offending keys; secret values are never included in messages.
/// Placeholder secrets ("change-me", "DEV-ONLY") are errors in strict environments (everything except Development, Testing
/// and Docker) and a loud warning in the local "Docker" demo environment.
/// </summary>
public static class ConfigurationValidator
{
    public const string DockerEnvironment = "Docker";
    public const string TestingEnvironment = "Testing";
    private static readonly string[] PlaceholderMarkers = ["change-me", "DEV-ONLY"];

    public sealed record Result(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
    {
        public bool IsValid => Errors.Count == 0;
    }

    public static bool IsStrict(IHostEnvironment env) =>
        !env.IsDevelopment() && !env.IsEnvironment(TestingEnvironment) && !env.IsEnvironment(DockerEnvironment);

    public static Result Validate(IConfiguration config, IHostEnvironment env)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var strict = IsStrict(env);

        // ---- Connection strings
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("Default")))
        {
            errors.Add("ConnectionStrings:Default is missing");
        }

        var cache = config.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
        if (cache.Enabled && cache.UseRedis && string.IsNullOrWhiteSpace(config.GetConnectionString("Redis")))
        {
            errors.Add("ConnectionStrings:Redis is missing (required when Cache:UseRedis=true)");
        }

        if (cache.EntityTtlMinutes < 1 || cache.ListTtlMinutes < 1 || cache.CircuitBreakSeconds < 1)
        {
            errors.Add("Cache:EntityTtlMinutes, Cache:ListTtlMinutes and Cache:CircuitBreakSeconds must be >= 1");
        }

        // ---- JWT
        var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(jwt.Key))
        {
            errors.Add("Jwt:Key is missing");
        }
        else
        {
            var bytes = Encoding.UTF8.GetByteCount(jwt.Key);
            if (bytes < 32)
            {
                errors.Add($"Jwt:Key is too short ({bytes} bytes, at least 32 required)");
            }

            if (PlaceholderMarkers.Any(m => jwt.Key.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                const string message = "Jwt:Key is a placeholder value (contains 'change-me'/'DEV-ONLY'); generate a real key with scripts/deploy/new-secrets.ps1";
                if (strict)
                {
                    errors.Add(message);
                }
                else if (!env.IsDevelopment() && !env.IsEnvironment(TestingEnvironment))
                {
                    warnings.Add(message + " — tolerated only because this is the local Docker demo environment");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
        {
            errors.Add("Jwt:Issuer and Jwt:Audience are required");
        }

        if (jwt.AccessTokenMinutes is < 1 or > 1440)
        {
            errors.Add("Jwt:AccessTokenMinutes must be between 1 and 1440");
        }

        if (jwt.RefreshTokenDays is < 1 or > 90)
        {
            errors.Add("Jwt:RefreshTokenDays must be between 1 and 90");
        }

        if (jwt.RefreshReuseGraceSeconds is < 0 or > 300)
        {
            errors.Add("Jwt:RefreshReuseGraceSeconds must be between 0 and 300");
        }

        if (string.IsNullOrWhiteSpace(jwt.RefreshCookieName))
        {
            errors.Add("Jwt:RefreshCookieName is required");
        }

        // ---- CORS
        foreach (var origin in config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        {
            if (origin == "*" || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                errors.Add($"Cors:AllowedOrigins contains an invalid origin '{origin}' (absolute http(s) origins only, no '*')");
            }
        }

        // ---- Rate limiting
        var rl = config.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        if (rl.Enabled)
        {
            foreach (var (name, rule) in new[] { ("Global", rl.Global), ("Auth", rl.Auth) })
            {
                if (rule.PermitLimit < 1 || rule.WindowSeconds < 1)
                {
                    errors.Add($"RateLimiting:{name}:PermitLimit and RateLimiting:{name}:WindowSeconds must be >= 1");
                }
            }
        }
        else if (strict)
        {
            warnings.Add("RateLimiting:Enabled=false in a production environment");
        }

        // ---- Forwarded headers (trusted proxies)
        var fh = config.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        foreach (var network in fh.KnownNetworks)
        {
            if (!IPNetwork.TryParse(network, out _))
            {
                errors.Add($"ForwardedHeaders:KnownNetworks contains an invalid CIDR '{network}'");
            }
            else if (strict && network is "0.0.0.0/0" or "::/0")
            {
                errors.Add("ForwardedHeaders:KnownNetworks must not trust every address in a production environment");
            }
        }

        foreach (var proxy in fh.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                errors.Add($"ForwardedHeaders:KnownProxies contains an invalid IP '{proxy}'");
            }
        }

        // ---- Misc
        var recon = config.GetSection(ReconciliationOptions.SectionName).Get<ReconciliationOptions>() ?? new ReconciliationOptions();
        if (recon.IntervalMinutes < 1)
        {
            errors.Add("Reconciliation:IntervalMinutes must be >= 1");
        }

        var commandTimeout = config.GetValue("Database:CommandTimeoutSeconds", 30);
        if (commandTimeout is < 1 or > 600)
        {
            errors.Add("Database:CommandTimeoutSeconds must be between 1 and 600");
        }

        return new Result(errors, warnings);
    }
}
