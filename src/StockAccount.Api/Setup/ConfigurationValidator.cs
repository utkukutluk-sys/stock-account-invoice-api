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

    /// <summary>RFC 1918 / loopback / unique-local / link-local ranges a trusted proxy network may lie in.</summary>
    private static readonly IPNetwork[] PrivateRanges =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
    ];

    /// <summary>True when the whole network is contained in one private/loopback range.</summary>
    public static bool IsPrivateOrLoopback(IPNetwork network) =>
        PrivateRanges.Any(r => r.BaseAddress.AddressFamily == network.BaseAddress.AddressFamily
            && network.PrefixLength >= r.PrefixLength
            && r.Contains(network.BaseAddress));

    private static bool UsesSaLogin(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return string.Equals(builder.UserID, "sa", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // malformed strings fail later with a clear SqlClient error
        }
    }

    public static bool IsStrict(IHostEnvironment env) =>
        !env.IsDevelopment() && !env.IsEnvironment(TestingEnvironment) && !env.IsEnvironment(DockerEnvironment);

    public static Result Validate(IConfiguration config, IHostEnvironment env, bool migrateOnly = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var strict = IsStrict(env);

        // ---- Connection strings
        var defaultConnection = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(defaultConnection))
        {
            errors.Add("ConnectionStrings:Default is missing");
        }
        else if (strict && !migrateOnly && UsesSaLogin(defaultConnection))
        {
            errors.Add("ConnectionStrings:Default must not use the 'sa' login at runtime; use the least-privilege app login (APP_DB_USER)");
        }

        var appLogin = config.GetValue<string>("Database:AppLogin:Name");
        if (!string.IsNullOrWhiteSpace(appLogin))
        {
            if (!StockAccount.Infrastructure.Startup.DatabaseProvisioner.LoginNameRegex().IsMatch(appLogin) || string.Equals(appLogin, "sa", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Database:AppLogin:Name is invalid (letters, digits and '_' only, not 'sa')");
            }

            var appPassword = config.GetValue<string>("Database:AppLogin:Password") ?? string.Empty;
            if (appPassword.Length is < 8 or > 128)
            {
                errors.Add("Database:AppLogin:Password is missing or not 8-128 characters");
            }
        }

        var recoveryModel = config.GetValue<string>("Database:RecoveryModel");
        if (!string.IsNullOrWhiteSpace(recoveryModel)
            && !StockAccount.Infrastructure.Startup.DatabaseProvisioner.RecoveryModels.Contains(recoveryModel.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("Database:RecoveryModel must be SIMPLE, FULL or BULK_LOGGED");
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
            if (!IPNetwork.TryParse(network, out var parsed))
            {
                errors.Add($"ForwardedHeaders:KnownNetworks contains an invalid CIDR '{network}'");
            }
            else if (strict && !IsPrivateOrLoopback(parsed))
            {
                // Coverage-based (BUG-11): 0.0.0.0/0, 0.0.0.0/1 + 128.0.0.0/1, 8.0.0.0/8 … are all rejected.
                errors.Add($"ForwardedHeaders:KnownNetworks '{network}' is not inside a private/loopback range; trust explicit proxy IPs (KnownProxies) instead");
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
