using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Tenancy;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Stocks.Dtos;
using StockAccount.Application.Features.Transactions;
using StockAccount.Application.Features.Transactions.Dtos;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Stocks;

public sealed class StockService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IDatabaseErrorInspector errors,
    ICacheService cache,
    ICacheInvalidator cacheInvalidator,
    ITransactionService transactions,
    IOptions<CacheOptions> cacheOptions) : IStockService
{
    public static readonly SortMap<Stock> Sorts = new SortMap<Stock>("code", s => s.Id)
        .Add("code", s => s.Code)
        .Add("name", s => s.Name)
        .Add("unit", s => s.Unit)
        .Add("unitPrice", s => s.UnitPrice)
        .Add("balance", s => s.Balance)
        .Add("createdAt", s => s.CreatedAt);

    public async Task<Cached<PagedResult<StockDto>>> ListAsync(StockListQuery query, CancellationToken cancellationToken = default)
    {
        var scope = currentUser.CacheScope();
        var version = await cache.GetVersionAsync(CacheKeys.Stock, scope, cancellationToken);
        var key = CacheKeys.List(CacheKeys.Stock, scope, version, CacheKeys.QueryHash(query, query.Sort ?? Sorts.DefaultSort));

        return await cache.GetOrAddAsync(key, cacheOptions.Value.ListTtl, ct => QueryListAsync(query, ct), cancellationToken);
    }

    public async Task<Cached<StockDto>> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var result = await cache.GetOrAddAsync(
            CacheKeys.Entity(CacheKeys.Stock, id),
            cacheOptions.Value.EntityTtl,
            async ct => await db.Stocks.AsNoTracking().Where(s => s.Id == id).Select(StockMappings.Projection).FirstOrDefaultAsync(ct)
                ?? throw NotFoundException.For("Stok"),
            cancellationToken);

        // By-id keys are shared across tenants: check access after reading (CONTRACT §6).
        return currentUser.CanAccessCompany(result.Value.CompanyId) ? result : throw NotFoundException.For("Stok");
    }

    public async Task<StockDto> CreateAsync(StockSaveRequest request, CancellationToken cancellationToken = default)
    {
        var companyId = currentUser.RequireCompanyId();
        await EnsureCompanyExistsAsync(companyId, cancellationToken);

        var stock = new Stock { CompanyId = companyId };
        Apply(stock, request);
        await EnsureUniqueCodeAsync(companyId, stock.Code, null, cancellationToken);

        db.Stocks.Add(stock);
        await db.SaveOrConflictAsync(errors, _ => DuplicateCode(), cancellationToken);
        await cacheInvalidator.StockChangedAsync(companyId, [stock.Id], cancellationToken);
        return await LoadDtoAsync(stock.Id, cancellationToken);
    }

    public async Task<StockDto> UpdateAsync(int id, StockSaveRequest request, CancellationToken cancellationToken = default)
    {
        var rowVersion = ValidationHelpers.ParseRowVersion(request.RowVersion);
        var stock = await FindTrackedAsync(id, cancellationToken);
        db.UseClientRowVersion(stock, rowVersion);

        Apply(stock, request);
        await EnsureUniqueCodeAsync(stock.CompanyId, stock.Code, id, cancellationToken);

        await db.SaveOrConflictAsync(errors, _ => DuplicateCode(), cancellationToken);
        await cacheInvalidator.StockChangedAsync(stock.CompanyId, [id], cancellationToken);
        return await LoadDtoAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var stock = await FindTrackedAsync(id, cancellationToken);
        if (stock.Balance != 0)
        {
            throw new ConflictException(ErrorCodes.StockHasBalance, "Bakiyesi sıfır olmayan stok silinemez.");
        }

        if (await db.InvoiceLines.AnyAsync(l => l.StockId == id && l.Invoice!.Status == InvoiceStatus.Draft, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.StockInUse, "Stok taslak bir faturada kullanıldığı için silinemez.");
        }

        db.Stocks.Remove(stock); // soft delete (interceptor)
        await db.SaveOrConflictAsync(errors, _ => DuplicateCode(), cancellationToken);
        await cacheInvalidator.StockChangedAsync(stock.CompanyId, [id], cancellationToken);
    }

    public async Task<PagedResult<StockTransDto>> ListTransactionsAsync(int id, StockTransListQuery query, CancellationToken cancellationToken = default)
    {
        if (!await db.Stocks.ForTenant(currentUser).AnyAsync(s => s.Id == id, cancellationToken))
        {
            throw NotFoundException.For("Stok");
        }

        query.StockId = id;
        return await transactions.ListStockTransAsync(query, cancellationToken);
    }

    private Task<PagedResult<StockDto>> QueryListAsync(StockListQuery query, CancellationToken cancellationToken)
    {
        var q = db.Stocks.AsNoTracking().ForTenant(currentUser);
        if (query.Search is { } s)
        {
            var code = DomainRules.NormalizeCode(s);
            q = q.Where(x => x.Code.Contains(code) || x.Code.Contains(s) || x.Name.Contains(s) || (x.Barcode != null && x.Barcode.Contains(s)));
        }

        if (query.Unit is StockUnit unit)
        {
            q = q.Where(x => x.Unit == unit);
        }

        if (query.IsActive is bool active)
        {
            q = q.Where(x => x.IsActive == active);
        }

        return q.ApplySort(query.Sort, Sorts).Select(StockMappings.Projection).ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
    }

    private async Task<StockDto> LoadDtoAsync(int id, CancellationToken cancellationToken) =>
        await db.Stocks.AsNoTracking().Where(s => s.Id == id).Select(StockMappings.Projection).FirstAsync(cancellationToken);

    private async Task<Stock> FindTrackedAsync(int id, CancellationToken cancellationToken) =>
        await db.Stocks.ForTenant(currentUser).FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
        ?? throw NotFoundException.For("Stok");

    private async Task EnsureCompanyExistsAsync(int companyId, CancellationToken cancellationToken)
    {
        if (!await db.Companies.AnyAsync(c => c.Id == companyId, cancellationToken))
        {
            throw new BusinessRuleException(ErrorCodes.InvalidReference, "Seçili firma bulunamadı.");
        }
    }

    private async Task EnsureUniqueCodeAsync(int companyId, string code, int? exceptId, CancellationToken cancellationToken)
    {
        if (await db.Stocks.AnyAsync(s => s.CompanyId == companyId && s.Code == code && s.Id != exceptId, cancellationToken))
        {
            throw DuplicateCode();
        }
    }

    private static ConflictException DuplicateCode() => new(ErrorCodes.DuplicateCode, "Bu stok kodu zaten kullanılıyor.");

    private static void Apply(Stock stock, StockSaveRequest r)
    {
        stock.Code = DomainRules.NormalizeCode(r.Code);
        stock.Name = r.Name.Trim();
        stock.Unit = r.Unit;
        stock.Barcode = ValidationHelpers.Clean(r.Barcode);
        stock.VatRate = r.VatRate;
        stock.UnitPrice = r.UnitPrice;
        stock.Description = ValidationHelpers.Clean(r.Description);
        stock.IsActive = r.IsActive;
    }
}
