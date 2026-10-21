using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Common.Security;

namespace StockAccount.Infrastructure.Identity;

/// <summary>Issues HS256 access tokens with the contract claims (CONTRACT §4.1).</summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(AppUser user, string role)
    {
        var o = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(o.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [AppClaimTypes.Subject] = user.Id.ToString(CultureInfo.InvariantCulture),
            [AppClaimTypes.Email] = user.Email ?? string.Empty,
            [AppClaimTypes.Name] = user.FullName,
            [AppClaimTypes.Role] = role,
            [AppClaimTypes.JwtId] = Guid.NewGuid().ToString("N"),
        };
        if (user.CompanyId is int companyId)
        {
            claims[AppClaimTypes.CompanyId] = companyId.ToString(CultureInfo.InvariantCulture);
        }

        if (user.CurrentAccountId is int accountId)
        {
            claims[AppClaimTypes.AccountId] = accountId.ToString(CultureInfo.InvariantCulture);
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(CreateSigningKey(o.Key), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expires);
    }

    /// <summary>Symmetric key from <c>Jwt:Key</c> (must be at least 32 characters).</summary>
    public static SymmetricSecurityKey CreateSigningKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:Key must be configured and at least 32 characters long.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }
}
