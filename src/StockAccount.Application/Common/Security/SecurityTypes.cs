using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using StockAccount.Application.Common.Identity;

namespace StockAccount.Application.Common.Security;

/// <summary>JWT claim names (CONTRACT §4.1). Inbound claim mapping is disabled, so these are the raw JWT names.</summary>
public static class AppClaimTypes
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    public const string Role = "role";
    public const string CompanyId = "company_id";
    public const string AccountId = "account_id";
    public const string JwtId = "jti";
}

/// <summary>Authorization policy names (CONTRACT §4.2).</summary>
public static class Policies
{
    public const string AdminOnly = "AdminOnly";
    public const string Operator = "Operator";
    public const string Portal = "Portal";
    public const string Authenticated = "Authenticated";
}

/// <summary>Configuration section "Jwt" (CONTRACT §4.1, §8.1).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "StockAccount";
    public string Audience { get; set; } = "StockAccount.Clients";
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
    public string RefreshCookieName { get; set; } = "sa_refresh";
    public int RefreshReuseGraceSeconds { get; set; } = 30;
}

/// <summary>Issued access token.</summary>
public sealed record AccessToken(string Token, DateTime ExpiresAt);

/// <summary>JWT access token issuing (implemented in Infrastructure).</summary>
public interface ITokenService
{
    AccessToken CreateAccessToken(AppUser user, string role);
}

/// <summary>Refresh token = 64 random bytes, Base64Url; only the SHA-256 hex hash is stored (CONTRACT §4.1).</summary>
public static class RefreshTokenGenerator
{
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));

    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
