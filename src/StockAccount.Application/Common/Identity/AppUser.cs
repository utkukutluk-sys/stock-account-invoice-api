using Microsoft.AspNetCore.Identity;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;

namespace StockAccount.Application.Common.Identity;

/// <summary>
/// Identity user (table AspNetUsers). Lives in Application (not Domain) because it derives from
/// <see cref="IdentityUser{TKey}"/> and the Domain project must stay package-free (see docs/DB_NOTES.md).
/// </summary>
public class AppUser : IdentityUser<int>, IHasCreatedAt
{
    public string FullName { get; set; } = string.Empty;

    /// <summary>Required for role User, null for Admin.</summary>
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    /// <summary>Set only for portal users (role User); unique when not null.</summary>
    public int? CurrentAccountId { get; set; }
    public CurrentAccount? CurrentAccount { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    public List<RefreshToken> RefreshTokens { get; set; } = [];
}
