using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Stocks.Dtos;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Application.Features.Stocks;

public interface IStockService
{
    /// <summary>Cached list (versioned list key, CONTRACT §6).</summary>
    Task<Cached<PagedResult<StockDto>>> ListAsync(StockListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Cached by id; tenant check happens after reading.</summary>
    Task<Cached<StockDto>> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<StockDto> CreateAsync(StockSaveRequest request, CancellationToken cancellationToken = default);
    Task<StockDto> UpdateAsync(int id, StockSaveRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Ledger rows of one stock (404 if the stock is not visible).</summary>
    Task<PagedResult<StockTransDto>> ListTransactionsAsync(int id, StockTransListQuery query, CancellationToken cancellationToken = default);
}
