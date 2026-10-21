using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Tenancy;
using StockAccount.Application.Features.Transactions.Dtos;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Transactions;

public sealed class TransactionService(IApplicationDbContext db, ICurrentUser currentUser) : ITransactionService
{
    public const string DefaultSort = "-transDate,-id";

    public static readonly SortMap<StockTrans> StockSorts = new SortMap<StockTrans>(DefaultSort, t => t.Id)
        .Add("transDate", t => t.TransDate)
        .Add("quantity", t => t.Quantity)
        .Add("amount", t => t.Amount)
        .Add("createdAt", t => t.CreatedAt);

    public static readonly SortMap<ActTrans> ActSorts = new SortMap<ActTrans>(DefaultSort, t => t.Id)
        .Add("transDate", t => t.TransDate)
        .Add("amount", t => t.Amount)
        .Add("createdAt", t => t.CreatedAt);

    public Task<PagedResult<StockTransDto>> ListStockTransAsync(StockTransListQuery query, CancellationToken cancellationToken = default)
    {
        // History query: keep rows whose stock was soft-deleted (DB_NOTES §2.5); tenant scoping is explicit.
        var q = db.StockTrans.AsNoTracking().IgnoreQueryFilters([QueryFilterNames.SoftDelete]).ForTenant(currentUser);

        if (query.StockId is int stockId)
        {
            q = q.Where(t => t.StockId == stockId);
        }

        if (query.InvoiceId is int invoiceId)
        {
            q = q.Where(t => t.InvoiceId == invoiceId);
        }

        if (query.Direction is StockTransDirection direction)
        {
            q = q.Where(t => t.Direction == direction);
        }

        if (query.DateFrom is DateOnly from)
        {
            q = q.Where(t => t.TransDate >= from);
        }

        if (query.DateTo is DateOnly to)
        {
            q = q.Where(t => t.TransDate <= to);
        }

        if (query.Search is { } s)
        {
            var code = DomainRules.NormalizeCode(s);
            q = q.Where(t => t.Stock!.Code.Contains(code) || t.Stock.Name.Contains(s) || t.Description.Contains(s));
        }

        return q.ApplySort(query.Sort, StockSorts)
            .Select(TransactionMappings.StockProjection)
            .ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
    }

    public Task<PagedResult<ActTransDto>> ListActTransAsync(ActTransListQuery query, CancellationToken cancellationToken = default)
    {
        var q = db.ActTrans.AsNoTracking().IgnoreQueryFilters([QueryFilterNames.SoftDelete]).ForTenant(currentUser);

        if (query.CurrentAccountId is int accountId)
        {
            q = q.Where(t => t.CurrentAccountId == accountId);
        }

        if (query.InvoiceId is int invoiceId)
        {
            q = q.Where(t => t.InvoiceId == invoiceId);
        }

        if (query.Direction is ActTransDirection direction)
        {
            q = q.Where(t => t.Direction == direction);
        }

        if (query.DateFrom is DateOnly from)
        {
            q = q.Where(t => t.TransDate >= from);
        }

        if (query.DateTo is DateOnly to)
        {
            q = q.Where(t => t.TransDate <= to);
        }

        if (query.Search is { } s)
        {
            var code = DomainRules.NormalizeCode(s);
            q = q.Where(t => t.CurrentAccount!.Code.Contains(code) || t.CurrentAccount.Title.Contains(s) || t.Description.Contains(s));
        }

        return q.ApplySort(query.Sort, ActSorts)
            .Select(TransactionMappings.ActProjection)
            .ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
    }
}
