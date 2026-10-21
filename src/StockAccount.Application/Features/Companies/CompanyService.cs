using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Companies.Dtos;
using StockAccount.Domain.Entities;

namespace StockAccount.Application.Features.Companies;

public sealed class CompanyService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IDatabaseErrorInspector errors,
    ICacheInvalidator cacheInvalidator) : ICompanyService
{
    public static readonly SortMap<Company> Sorts = new SortMap<Company>("name", c => c.Id)
        .Add("name", c => c.Name)
        .Add("taxNumber", c => c.TaxNumber)
        .Add("city", c => c.City)
        .Add("createdAt", c => c.CreatedAt);

    public async Task<PagedResult<CompanyDto>> ListAsync(CompanyListQuery query, CancellationToken cancellationToken = default)
    {
        var q = db.Companies.AsNoTracking();
        if (query.Search is { } s)
        {
            q = q.Where(c => c.Name.Contains(s) || c.TaxNumber.Contains(s) || (c.City != null && c.City.Contains(s)));
        }

        if (query.IsActive is bool active)
        {
            q = q.Where(c => c.IsActive == active);
        }

        var page = await q.ApplySort(query.Sort, Sorts).ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
        return new PagedResult<CompanyDto>(page.Items.Select(c => c.ToDto()).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<CompanyDto> GetAsync(int id, CancellationToken cancellationToken = default) =>
        (await FindAsync(id, tracked: false, cancellationToken)).ToDto();

    public async Task<CompanyDto> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var id = currentUser.CompanyId ?? throw NotFoundException.For("Firma");
        return await GetAsync(id, cancellationToken);
    }

    public async Task<CompanyDto> CreateAsync(CompanySaveRequest request, CancellationToken cancellationToken = default)
    {
        var company = new Company();
        Apply(company, request);
        await EnsureUniqueTaxNumberAsync(company.TaxNumber, null, cancellationToken);

        db.Companies.Add(company);
        await db.SaveOrConflictAsync(errors, _ => DuplicateTaxNumber(), cancellationToken);
        return company.ToDto();
    }

    public async Task<CompanyDto> UpdateAsync(int id, CompanySaveRequest request, CancellationToken cancellationToken = default)
    {
        var rowVersion = ValidationHelpers.ParseRowVersion(request.RowVersion);
        var company = await FindAsync(id, tracked: true, cancellationToken);
        db.UseClientRowVersion(company, rowVersion);

        Apply(company, request);
        await EnsureUniqueTaxNumberAsync(company.TaxNumber, id, cancellationToken);

        await db.SaveOrConflictAsync(errors, _ => DuplicateTaxNumber(), cancellationToken);
        await cacheInvalidator.CompanyChangedAsync(id, cancellationToken);
        return company.ToDto();
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var company = await FindAsync(id, tracked: true, cancellationToken);

        var notEmpty = await db.Stocks.AnyAsync(s => s.CompanyId == id, cancellationToken)
            || await db.CurrentAccounts.AnyAsync(a => a.CompanyId == id, cancellationToken)
            || await db.Invoices.AnyAsync(i => i.CompanyId == id, cancellationToken)
            || await db.Users.AnyAsync(u => u.CompanyId == id, cancellationToken);
        if (notEmpty)
        {
            throw new ConflictException(ErrorCodes.CompanyNotEmpty, "Firmaya ait stok, cari, fatura veya kullanıcı bulunduğu için silinemez.");
        }

        db.Companies.Remove(company);
        await db.SaveOrConflictAsync(errors, _ => DuplicateTaxNumber(), cancellationToken);
        await cacheInvalidator.CompanyChangedAsync(id, cancellationToken);
    }

    private async Task<Company> FindAsync(int id, bool tracked, CancellationToken cancellationToken)
    {
        var q = tracked ? db.Companies : db.Companies.AsNoTracking();
        return await q.FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw NotFoundException.For("Firma");
    }

    private async Task EnsureUniqueTaxNumberAsync(string taxNumber, int? exceptId, CancellationToken cancellationToken)
    {
        if (await db.Companies.AnyAsync(c => c.TaxNumber == taxNumber && c.Id != exceptId, cancellationToken))
        {
            throw DuplicateTaxNumber();
        }
    }

    private static ConflictException DuplicateTaxNumber() =>
        new(ErrorCodes.DuplicateTaxNumber, "Bu vergi numarası ile kayıtlı bir firma zaten var.");

    private static void Apply(Company company, CompanySaveRequest r)
    {
        company.Name = r.Name.Trim();
        company.TaxNumber = r.TaxNumber.Trim();
        company.TaxOffice = ValidationHelpers.Clean(r.TaxOffice);
        company.Phone = ValidationHelpers.Clean(r.Phone);
        company.Email = ValidationHelpers.Clean(r.Email);
        company.Address = ValidationHelpers.Clean(r.Address);
        company.City = ValidationHelpers.Clean(r.City);
        company.IsActive = r.IsActive;
    }
}
