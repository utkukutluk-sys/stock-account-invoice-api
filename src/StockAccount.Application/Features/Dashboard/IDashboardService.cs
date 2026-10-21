using StockAccount.Application.Features.Dashboard.Dtos;

namespace StockAccount.Application.Features.Dashboard;

public interface IDashboardService
{
    /// <summary>Tenant-scoped summary; "this month" = current UTC month by InvoiceDate, Approved only.</summary>
    Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
}
