using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.CurrentAccounts.Dtos;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Application.Features.CurrentAccounts;

public interface ICurrentAccountService
{
    Task<Cached<PagedResult<CurrentAccountDto>>> ListAsync(CurrentAccountListQuery query, CancellationToken cancellationToken = default);
    Task<Cached<CurrentAccountDto>> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<CurrentAccountDto> CreateAsync(CurrentAccountSaveRequest request, CancellationToken cancellationToken = default);
    Task<CurrentAccountDto> UpdateAsync(int id, CurrentAccountSaveRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<PagedResult<ActTransDto>> ListTransactionsAsync(int id, ActTransListQuery query, CancellationToken cancellationToken = default);
}
