using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Companies.Dtos;

namespace StockAccount.Application.Features.Companies;

public interface ICompanyService
{
    Task<PagedResult<CompanyDto>> ListAsync(CompanyListQuery query, CancellationToken cancellationToken = default);
    Task<CompanyDto> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The caller's company (admin: X-Company-Id company, else 404).</summary>
    Task<CompanyDto> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task<CompanyDto> CreateAsync(CompanySaveRequest request, CancellationToken cancellationToken = default);
    Task<CompanyDto> UpdateAsync(int id, CompanySaveRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
