using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Invoices.Dtos;

namespace StockAccount.Application.Features.Invoices;

public interface IInvoiceService
{
    Task<PagedResult<InvoiceListItemDto>> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken = default);
    Task<InvoiceDto> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<InvoiceDto> CreateAsync(InvoiceSaveRequest request, CancellationToken cancellationToken = default);
    Task<InvoiceDto> UpdateAsync(int id, InvoiceSaveRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Draft → Approved in one DB transaction with retry (CONTRACT §3.6). Writes ledgers and updates balances.</summary>
    Task<InvoiceDto> ApproveAsync(int id, InvoiceApproveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Approved → Cancelled; writes reversal rows (CONTRACT §3.2).</summary>
    Task<InvoiceDto> CancelAsync(int id, InvoiceCancelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Configuration section "Inventory".</summary>
public sealed class InventoryOptions
{
    public const string SectionName = "Inventory";

    public bool AllowNegativeStock { get; set; }
}
