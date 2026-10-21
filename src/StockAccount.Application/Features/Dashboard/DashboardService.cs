using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Tenancy;
using StockAccount.Application.Features.Dashboard.Dtos;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Dashboard;

public sealed class DashboardService(IApplicationDbContext db, ICurrentUser currentUser, TimeProvider timeProvider) : IDashboardService
{
    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var nextMonth = monthStart.AddMonths(1);

        var stocks = db.Stocks.AsNoTracking().ForTenant(currentUser);
        var accounts = db.CurrentAccounts.AsNoTracking().ForTenant(currentUser);
        var invoices = db.Invoices.AsNoTracking().IgnoreQueryFilters([QueryFilterNames.SoftDelete]).ForTenant(currentUser);
        var approvedThisMonth = invoices.Where(i => i.Status == InvoiceStatus.Approved && i.InvoiceDate >= monthStart && i.InvoiceDate < nextMonth);

        var stockCount = await stocks.CountAsync(cancellationToken);
        var accountCount = await accounts.CountAsync(cancellationToken);
        var draftCount = await invoices.CountAsync(i => i.Status == InvoiceStatus.Draft, cancellationToken);
        var approvedCount = await approvedThisMonth.CountAsync(cancellationToken);
        var salesTotal = await approvedThisMonth.Where(i => i.Type == InvoiceType.Sales).SumAsync(i => i.GrandTotal, cancellationToken);
        var purchaseTotal = await approvedThisMonth.Where(i => i.Type == InvoiceType.Purchase).SumAsync(i => i.GrandTotal, cancellationToken);
        var receivable = await accounts.Where(a => a.Balance > 0).SumAsync(a => a.Balance, cancellationToken);
        var payable = -await accounts.Where(a => a.Balance < 0).SumAsync(a => a.Balance, cancellationToken);

        var lowest = await stocks.Where(s => s.IsActive)
            .OrderBy(s => s.Balance).ThenBy(s => s.Id).Take(5)
            .Select(s => new LowStockDto(s.Id, s.Code, s.Name, s.Unit, s.Balance))
            .ToListAsync(cancellationToken);

        var recent = await invoices.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Take(5)
            .Select(InvoiceMappings.ListProjection)
            .ToListAsync(cancellationToken);

        return new DashboardSummaryDto(
            stockCount, accountCount, draftCount, approvedCount, salesTotal, purchaseTotal, receivable, payable, lowest, recent);
    }
}
