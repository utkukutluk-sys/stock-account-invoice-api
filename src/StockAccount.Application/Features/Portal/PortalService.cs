using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.CurrentAccounts.Dtos;
using StockAccount.Application.Features.Invoices;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Application.Features.Portal.Dtos;
using StockAccount.Application.Features.Transactions;
using StockAccount.Application.Features.Transactions.Dtos;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Portal;

public sealed class PortalService(IApplicationDbContext db, ICurrentUser currentUser, ITransactionService transactions) : IPortalService
{
    private int AccountId => currentUser.CurrentAccountId ?? throw new ForbiddenException("Bu sayfa yalnızca cari hesap kullanıcıları içindir.");

    public async Task<CurrentAccountDto> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var accountId = AccountId;
        return await db.CurrentAccounts.AsNoTracking().IgnoreQueryFilters([QueryFilterNames.SoftDelete])
            .Where(a => a.Id == accountId)
            .Select(CurrentAccountMappings.Projection)
            .FirstOrDefaultAsync(cancellationToken) ?? throw NotFoundException.For("Cari hesap");
    }

    public Task<PagedResult<ActTransDto>> ListTransactionsAsync(PortalTransactionListQuery query, CancellationToken cancellationToken = default) =>
        transactions.ListActTransAsync(
            new ActTransListQuery
            {
                CurrentAccountId = AccountId,
                DateFrom = query.DateFrom,
                DateTo = query.DateTo,
                Page = query.Page,
                PageSize = query.PageSize,
                Sort = query.Sort,
                Q = query.Q,
            },
            cancellationToken);

    public Task<PagedResult<InvoiceListItemDto>> ListInvoicesAsync(PortalInvoiceListQuery query, CancellationToken cancellationToken = default)
    {
        var q = OwnInvoices();
        if (query.Status is InvoiceStatus status)
        {
            q = q.Where(i => i.Status == status);
        }

        if (query.Search is { } s)
        {
            q = q.Where(i => (i.DocumentNo != null && i.DocumentNo.Contains(s)) || (i.Description != null && i.Description.Contains(s)));
        }

        return q.ApplySort(query.Sort, InvoiceService.Sorts)
            .Select(InvoiceMappings.ListProjection)
            .ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
    }

    public async Task<InvoiceDto> GetInvoiceAsync(int id, CancellationToken cancellationToken = default) =>
        await OwnInvoices().Where(i => i.Id == id).Select(InvoiceMappings.DetailProjection).FirstOrDefaultAsync(cancellationToken)
        ?? throw NotFoundException.For("Fatura");

    private IQueryable<Invoice> OwnInvoices()
    {
        var accountId = AccountId;
        return db.Invoices.AsNoTracking().IgnoreQueryFilters([QueryFilterNames.SoftDelete])
            .Where(i => i.CurrentAccountId == accountId && i.Status != InvoiceStatus.Draft);
    }
}
