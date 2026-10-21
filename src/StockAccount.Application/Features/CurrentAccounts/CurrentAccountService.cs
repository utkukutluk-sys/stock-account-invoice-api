using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Tenancy;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.CurrentAccounts.Dtos;
using StockAccount.Application.Features.Transactions;
using StockAccount.Application.Features.Transactions.Dtos;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.CurrentAccounts;

public sealed class CurrentAccountService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IDatabaseErrorInspector errors,
    ICacheService cache,
    ICacheInvalidator cacheInvalidator,
    ITransactionService transactions,
    IOptions<CacheOptions> cacheOptions) : ICurrentAccountService
{
    public static readonly SortMap<CurrentAccount> Sorts = new SortMap<CurrentAccount>("code", a => a.Id)
        .Add("code", a => a.Code)
        .Add("title", a => a.Title)
        .Add("type", a => a.Type)
        .Add("city", a => a.City)
        .Add("balance", a => a.Balance)
        .Add("createdAt", a => a.CreatedAt);

    public async Task<Cached<PagedResult<CurrentAccountDto>>> ListAsync(CurrentAccountListQuery query, CancellationToken cancellationToken = default)
    {
        var scope = currentUser.CacheScope();
        var version = await cache.GetVersionAsync(CacheKeys.Account, scope, cancellationToken);
        var key = CacheKeys.List(CacheKeys.Account, scope, version, CacheKeys.QueryHash(query, query.Sort ?? Sorts.DefaultSort));

        return await cache.GetOrAddAsync(key, cacheOptions.Value.ListTtl, ct => QueryListAsync(query, ct), cancellationToken);
    }

    public async Task<Cached<CurrentAccountDto>> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var result = await cache.GetOrAddAsync(
            CacheKeys.Entity(CacheKeys.Account, id),
            cacheOptions.Value.EntityTtl,
            async ct => await db.CurrentAccounts.AsNoTracking().Where(a => a.Id == id).Select(CurrentAccountMappings.Projection).FirstOrDefaultAsync(ct)
                ?? throw NotFoundException.For("Cari hesap"),
            cancellationToken);

        return currentUser.CanAccessCompany(result.Value.CompanyId) ? result : throw NotFoundException.For("Cari hesap");
    }

    public async Task<CurrentAccountDto> CreateAsync(CurrentAccountSaveRequest request, CancellationToken cancellationToken = default)
    {
        var companyId = currentUser.RequireCompanyId();
        if (!await db.Companies.AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            throw new BusinessRuleException(ErrorCodes.InvalidReference, "Seçili firma bulunamadı.");
        }

        var account = new CurrentAccount { CompanyId = companyId };
        Apply(account, request);
        await EnsureUniqueCodeAsync(companyId, account.Code, null, cancellationToken);

        db.CurrentAccounts.Add(account);
        await db.SaveOrConflictAsync(errors, _ => DuplicateCode(), cancellationToken);
        await cacheInvalidator.AccountChangedAsync(companyId, account.Id, cancellationToken);
        return await LoadDtoAsync(account.Id, cancellationToken);
    }

    public async Task<CurrentAccountDto> UpdateAsync(int id, CurrentAccountSaveRequest request, CancellationToken cancellationToken = default)
    {
        var rowVersion = ValidationHelpers.ParseRowVersion(request.RowVersion);
        var account = await FindTrackedAsync(id, cancellationToken);
        db.UseClientRowVersion(account, rowVersion);

        Apply(account, request);
        await EnsureUniqueCodeAsync(account.CompanyId, account.Code, id, cancellationToken);

        await db.SaveOrConflictAsync(errors, _ => DuplicateCode(), cancellationToken);
        await cacheInvalidator.AccountChangedAsync(account.CompanyId, id, cancellationToken);
        return await LoadDtoAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var account = await FindTrackedAsync(id, cancellationToken);
        if (account.Balance != 0)
        {
            throw new ConflictException(ErrorCodes.AccountHasBalance, "Bakiyesi sıfır olmayan cari hesap silinemez.");
        }

        if (await db.Invoices.AnyAsync(i => i.CurrentAccountId == id && i.Status == InvoiceStatus.Draft, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.AccountInUse, "Cari hesap taslak bir faturada kullanıldığı için silinemez.");
        }

        if (await db.Users.AnyAsync(u => u.CurrentAccountId == id, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.AccountInUse, "Cari hesap bir kullanıcıya bağlı olduğu için silinemez.");
        }

        db.CurrentAccounts.Remove(account); // soft delete (interceptor)
        await db.SaveOrConflictAsync(errors, _ => DuplicateCode(), cancellationToken);
        await cacheInvalidator.AccountChangedAsync(account.CompanyId, id, cancellationToken);
    }

    public async Task<PagedResult<ActTransDto>> ListTransactionsAsync(int id, ActTransListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await db.CurrentAccounts.ForTenant(currentUser).AnyAsync(a => a.Id == id, cancellationToken))
        {
            throw NotFoundException.For("Cari hesap");
        }

        query.CurrentAccountId = id;
        return await transactions.ListActTransAsync(query, cancellationToken);
    }

    private Task<PagedResult<CurrentAccountDto>> QueryListAsync(CurrentAccountListQuery query, CancellationToken cancellationToken)
    {
        var q = db.CurrentAccounts.AsNoTracking().ForTenant(currentUser);
        if (query.Search is { } s)
        {
            var code = DomainRules.NormalizeCode(s);
            q = q.Where(x => x.Code.Contains(code) || x.Code.Contains(s) || x.Title.Contains(s) || (x.TaxNumber != null && x.TaxNumber.Contains(s)));
        }

        if (query.Type is CurrentAccountType type)
        {
            q = q.Where(x => x.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.City))
        {
            var city = query.City.Trim();
            q = q.Where(x => x.City != null && x.City.Contains(city));
        }

        if (query.IsActive is bool active)
        {
            q = q.Where(x => x.IsActive == active);
        }

        return q.ApplySort(query.Sort, Sorts).Select(CurrentAccountMappings.Projection).ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
    }

    private async Task<CurrentAccountDto> LoadDtoAsync(int id, CancellationToken cancellationToken) =>
        await db.CurrentAccounts.AsNoTracking().Where(a => a.Id == id).Select(CurrentAccountMappings.Projection).FirstAsync(cancellationToken);

    private async Task<CurrentAccount> FindTrackedAsync(int id, CancellationToken cancellationToken) =>
        await db.CurrentAccounts.ForTenant(currentUser).FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
        ?? throw NotFoundException.For("Cari hesap");

    private async Task EnsureUniqueCodeAsync(int companyId, string code, int? exceptId, CancellationToken cancellationToken)
    {
        if (await db.CurrentAccounts.AnyAsync(a => a.CompanyId == companyId && a.Code == code && a.Id != exceptId, cancellationToken))
        {
            throw DuplicateCode();
        }
    }

    private static ConflictException DuplicateCode() => new(ErrorCodes.DuplicateCode, "Bu cari kodu zaten kullanılıyor.");

    private static void Apply(CurrentAccount account, CurrentAccountSaveRequest r)
    {
        account.Code = DomainRules.NormalizeCode(r.Code);
        account.Title = r.Title.Trim();
        account.Type = r.Type;
        account.TaxNumber = ValidationHelpers.Clean(r.TaxNumber);
        account.TaxOffice = ValidationHelpers.Clean(r.TaxOffice);
        account.Phone = ValidationHelpers.Clean(r.Phone);
        account.Email = ValidationHelpers.Clean(r.Email);
        account.Address = ValidationHelpers.Clean(r.Address);
        account.City = ValidationHelpers.Clean(r.City);
        account.IsActive = r.IsActive;
    }
}
