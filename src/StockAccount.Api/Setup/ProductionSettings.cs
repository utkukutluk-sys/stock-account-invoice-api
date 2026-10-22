namespace StockAccount.Api.Setup;

/// <summary>Section "RateLimiting" (production readiness). Limits are per authenticated user id, else per client IP.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";
    public const string AuthPolicy = "auth";

    public bool Enabled { get; set; } = true;

    /// <summary>Every API endpoint (health endpoints excluded).</summary>
    public RateLimitRule Global { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };

    /// <summary>POST /api/auth/login and /api/auth/refresh, per client IP (brute-force protection).</summary>
    public RateLimitRule Auth { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };
}

public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }
}

/// <summary>
/// Section "ForwardedHeaders": which reverse proxies may set X-Forwarded-For/Proto. Only these are trusted; an empty list
/// trusts loopback only (ASP.NET Core default). Docker: the private ranges of the compose network.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public string[] KnownNetworks { get; set; } = [];
    public string[] KnownProxies { get; set; } = [];
    public int ForwardLimit { get; set; } = 1;
}

/// <summary>Section "Swagger".</summary>
public sealed class SwaggerSettings
{
    public const string SectionName = "Swagger";

    /// <summary>Default: enabled except in Production.</summary>
    public bool? Enabled { get; set; }
}
