namespace StockAccount.Application.Common.Abstractions;

/// <summary>
/// The caller of the current request (CONTRACT §4.3). Implemented in Api from HttpContext.
/// Infrastructure's audit interceptor reads <see cref="UserId"/> (null for seeding/background work).
/// </summary>
public interface ICurrentUser
{
    int? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    bool IsAdmin { get; }

    /// <summary>company_id claim; for admins the X-Company-Id header (null = all companies).</summary>
    int? CompanyId { get; }

    /// <summary>account_id claim (portal users only).</summary>
    int? CurrentAccountId { get; }

    string? IpAddress { get; }
}
