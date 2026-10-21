using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Features.Reconciliation;
using StockAccount.Application.Features.Reconciliation.Dtos;
using StockAccount.Infrastructure.Persistence;

namespace StockAccount.Infrastructure.Reconciliation;

/// <summary>
/// Compares stored balances with ledger sums through the DB views <c>vw_StockBalanceReconciliation</c> /
/// <c>vw_AccountBalanceReconciliation</c> (all companies, incl. soft-deleted rows). Optionally fixes stored balances.
/// </summary>
public sealed class ReconciliationService(
    AppDbContext db,
    ICacheInvalidator cacheInvalidator,
    TimeProvider timeProvider,
    ILogger<ReconciliationService> logger) : IReconciliationService
{
    public const string StockEntity = "Stock";
    public const string AccountEntity = "CurrentAccount";

    public async Task<ReconciliationResultDto> RunAsync(bool fix, CancellationToken cancellationToken = default)
    {
        var checkedAt = timeProvider.GetUtcNow().UtcDateTime;
        var stocksChecked = await db.StockBalanceChecks.CountAsync(cancellationToken);
        var accountsChecked = await db.AccountBalanceChecks.CountAsync(cancellationToken);

        var stockIssues = await db.StockBalanceChecks.AsNoTracking().Where(x => x.Difference != 0).OrderBy(x => x.StockId).ToListAsync(cancellationToken);
        var accountIssues = await db.AccountBalanceChecks.AsNoTracking().Where(x => x.Difference != 0).OrderBy(x => x.CurrentAccountId).ToListAsync(cancellationToken);

        var discrepancies = stockIssues
            .Select(x => new ReconciliationDiscrepancyDto(StockEntity, x.StockId, x.CompanyId, x.Code, x.StoredBalance, x.LedgerBalance, x.Difference))
            .Concat(accountIssues.Select(x => new ReconciliationDiscrepancyDto(AccountEntity, x.CurrentAccountId, x.CompanyId, x.Code, x.StoredBalance, x.LedgerBalance, x.Difference)))
            .ToList();

        foreach (var d in discrepancies)
        {
            logger.LogWarning(
                "Balance discrepancy {EntityType} {EntityId} {Code} {StoredBalance} {LedgerBalance} {Difference}",
                d.EntityType, d.EntityId, d.Code, d.StoredBalance, d.LedgerBalance, d.Difference);
        }

        var fixedAny = false;
        if (fix && discrepancies.Count > 0)
        {
            fixedAny = await FixAsync(stockIssues.Select(x => (x.StockId, x.LedgerBalance)).ToList(), accountIssues.Select(x => (x.CurrentAccountId, x.LedgerBalance)).ToList(), cancellationToken);
            if (fixedAny)
            {
                foreach (var group in discrepancies.GroupBy(d => d.CompanyId))
                {
                    var stockIds = group.Where(d => d.EntityType == StockEntity).Select(d => d.EntityId).ToList();
                    if (stockIds.Count > 0)
                    {
                        await cacheInvalidator.StockChangedAsync(group.Key, stockIds, cancellationToken);
                    }

                    foreach (var account in group.Where(d => d.EntityType == AccountEntity))
                    {
                        await cacheInvalidator.AccountChangedAsync(group.Key, account.EntityId, cancellationToken);
                    }
                }
            }
        }

        logger.LogInformation(
            "Balance reconciliation finished: {StocksChecked} stocks, {AccountsChecked} accounts checked, {DiscrepancyCount} discrepancies, fixed: {Fixed}",
            stocksChecked, accountsChecked, discrepancies.Count, fixedAny);

        return new ReconciliationResultDto(checkedAt, stocksChecked, accountsChecked, fixedAny, discrepancies);
    }

    private async Task<bool> FixAsync(
        IReadOnlyList<(int Id, decimal Ledger)> stocks, IReadOnlyList<(int Id, decimal Ledger)> accounts, CancellationToken cancellationToken)
    {
        var stockIds = stocks.Select(s => s.Id).ToList();
        var accountIds = accounts.Select(a => a.Id).ToList();

        var stockEntities = await db.Stocks.IgnoreQueryFilters([QueryFilterNames.SoftDelete]).Where(s => stockIds.Contains(s.Id)).ToListAsync(cancellationToken);
        foreach (var stock in stockEntities)
        {
            stock.Balance = stocks.First(s => s.Id == stock.Id).Ledger;
        }

        var accountEntities = await db.CurrentAccounts.IgnoreQueryFilters([QueryFilterNames.SoftDelete]).Where(a => accountIds.Contains(a.Id)).ToListAsync(cancellationToken);
        foreach (var account in accountEntities)
        {
            account.Balance = accounts.First(a => a.Id == account.Id).Ledger;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Reconciliation fixed {StockCount} stock and {AccountCount} account balances", stockEntities.Count, accountEntities.Count);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A concurrent approve/cancel changed a balance: leave it for the next run.
            logger.LogWarning(ex, "Reconciliation fix skipped because of a concurrent update; the next run re-checks");
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
