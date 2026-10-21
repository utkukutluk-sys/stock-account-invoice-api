namespace StockAccount.Application.Common.Identity;

/// <summary>Refresh token (table RefreshTokens). Only the SHA-256 hex hash of the raw token is stored.</summary>
public class RefreshToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public AppUser? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByIp { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? RevokedReason { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAt is null && ExpiresAt > utcNow;
}
