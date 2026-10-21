using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Users.Dtos;

namespace StockAccount.Application.Features.Users;

public interface IUserService
{
    Task<PagedResult<UserDto>> ListAsync(UserListQuery query, CancellationToken cancellationToken = default);
    Task<UserDto> GetAsync(int id, CancellationToken cancellationToken = default);
    Task<UserDto> CreateAsync(UserCreateRequest request, CancellationToken cancellationToken = default);
    Task<UserDto> UpdateAsync(int id, UserUpdateRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
