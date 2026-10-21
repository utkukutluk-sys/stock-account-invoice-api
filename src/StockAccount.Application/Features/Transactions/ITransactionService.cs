using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Application.Features.Transactions;

/// <summary>Read-only ledger queries (StockTrans / ActTrans are immutable; written only by invoice approve/cancel).</summary>
public interface ITransactionService
{
    Task<PagedResult<StockTransDto>> ListStockTransAsync(StockTransListQuery query, CancellationToken cancellationToken = default);
    Task<PagedResult<ActTransDto>> ListActTransAsync(ActTransListQuery query, CancellationToken cancellationToken = default);
}
