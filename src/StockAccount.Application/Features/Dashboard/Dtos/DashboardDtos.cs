using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Dashboard.Dtos;

public sealed record LowStockDto(int Id, string Code, string Name, StockUnit Unit, decimal Balance);

public sealed record DashboardSummaryDto(
    int StockCount,
    int CurrentAccountCount,
    int DraftInvoiceCount,
    int ApprovedInvoiceCountThisMonth,
    decimal SalesTotalThisMonth,
    decimal PurchaseTotalThisMonth,
    decimal TotalReceivable,
    decimal TotalPayable,
    IReadOnlyList<LowStockDto> LowestStocks,
    IReadOnlyList<InvoiceListItemDto> RecentInvoices);
