using System.Globalization;

namespace StockAccount.Application.Common.Caching;

/// <summary>Invalidation rules of CONTRACT §6 (call only after a successful commit).</summary>
public interface ICacheInvalidator
{
    /// <summary>Stock create/update/delete: remove <c>stock:{id}</c>, bump <c>stock:ver:{companyId}</c> and <c>stock:ver:all</c>.</summary>
    Task StockChangedAsync(int companyId, IEnumerable<int> stockIds, CancellationToken cancellationToken = default);

    /// <summary>Current account create/update/delete: same with <c>account</c>.</summary>
    Task AccountChangedAsync(int companyId, int accountId, CancellationToken cancellationToken = default);

    /// <summary>Invoice approve/cancel: affected stocks and the account.</summary>
    Task InvoicePostedAsync(int companyId, IEnumerable<int> stockIds, int accountId, CancellationToken cancellationToken = default);

    /// <summary>Company update/delete: bump stock and account list versions for the company and "all".</summary>
    Task CompanyChangedAsync(int companyId, CancellationToken cancellationToken = default);
}

public sealed class CacheInvalidator(ICacheService cache) : ICacheInvalidator
{
    public async Task StockChangedAsync(int companyId, IEnumerable<int> stockIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in stockIds.Distinct())
        {
            await cache.RemoveAsync(CacheKeys.Entity(CacheKeys.Stock, id), cancellationToken);
        }

        await BumpAsync(CacheKeys.Stock, companyId, cancellationToken);
    }

    public async Task AccountChangedAsync(int companyId, int accountId, CancellationToken cancellationToken = default)
    {
        await cache.RemoveAsync(CacheKeys.Entity(CacheKeys.Account, accountId), cancellationToken);
        await BumpAsync(CacheKeys.Account, companyId, cancellationToken);
    }

    public async Task InvoicePostedAsync(int companyId, IEnumerable<int> stockIds, int accountId, CancellationToken cancellationToken = default)
    {
        await StockChangedAsync(companyId, stockIds, cancellationToken);
        await AccountChangedAsync(companyId, accountId, cancellationToken);
    }

    public async Task CompanyChangedAsync(int companyId, CancellationToken cancellationToken = default)
    {
        await BumpAsync(CacheKeys.Stock, companyId, cancellationToken);
        await BumpAsync(CacheKeys.Account, companyId, cancellationToken);
    }

    private async Task BumpAsync(string entity, int companyId, CancellationToken cancellationToken)
    {
        await cache.BumpVersionAsync(entity, companyId.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await cache.BumpVersionAsync(entity, CacheKeys.AllScope, cancellationToken);
    }
}
