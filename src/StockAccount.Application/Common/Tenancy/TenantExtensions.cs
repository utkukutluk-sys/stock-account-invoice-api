using System.Globalization;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Domain.Common;

namespace StockAccount.Application.Common.Tenancy;

/// <summary>Explicit tenant scoping (CONTRACT §3.8; there is no global tenant filter).</summary>
public static class TenantExtensions
{
    /// <summary>Filters to the caller's company (non-admin claim, or admin X-Company-Id). Admin without header → all companies.</summary>
    public static IQueryable<T> ForTenant<T>(this IQueryable<T> query, ICurrentUser user)
        where T : class, ITenantEntity =>
        user.CompanyId is int companyId ? query.Where(e => e.CompanyId == companyId) : query;

    /// <summary>Company for creating tenant entities; admin without X-Company-Id → 400 COMPANY_REQUIRED.</summary>
    public static int RequireCompanyId(this ICurrentUser user) =>
        user.CompanyId ?? throw new BadRequestException(ErrorCodes.CompanyRequired, "Lütfen önce firma seçiniz.");

    /// <summary>True when the caller may see a row of <paramref name="companyId"/> (used for cached by-id entries).</summary>
    public static bool CanAccessCompany(this ICurrentUser user, int companyId) =>
        user.CompanyId is not int scope || scope == companyId;

    /// <summary>Cache scope: company id or "all" (admin without X-Company-Id).</summary>
    public static string CacheScope(this ICurrentUser user) =>
        user.CompanyId?.ToString(CultureInfo.InvariantCulture) ?? "all";
}
