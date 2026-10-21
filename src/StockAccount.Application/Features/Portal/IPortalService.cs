using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.CurrentAccounts.Dtos;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Application.Features.Portal.Dtos;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Application.Features.Portal;

/// <summary>Portal user (role User + account_id) views of his own current account (CONTRACT §5.10).</summary>
public interface IPortalService
{
    Task<CurrentAccountDto> GetAccountAsync(CancellationToken cancellationToken = default);
    Task<PagedResult<ActTransDto>> ListTransactionsAsync(PortalTransactionListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Only Approved/Cancelled invoices are ever returned.</summary>
    Task<PagedResult<InvoiceListItemDto>> ListInvoicesAsync(PortalInvoiceListQuery query, CancellationToken cancellationToken = default);

    /// <summary>404 if not own or Draft.</summary>
    Task<InvoiceDto> GetInvoiceAsync(int id, CancellationToken cancellationToken = default);
}
