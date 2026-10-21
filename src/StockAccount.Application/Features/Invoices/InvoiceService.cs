using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Tenancy;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Invoices;

public sealed class InvoiceService(
    IApplicationDbContext db,
    ICurrentUser currentUser,
    IDatabaseErrorInspector errors,
    ICacheInvalidator cacheInvalidator,
    IOptions<InventoryOptions> inventoryOptions,
    TimeProvider timeProvider,
    ILogger<InvoiceService> logger) : IInvoiceService
{
    /// <summary>Approve/cancel attempts on concurrency or numbering conflicts (CONTRACT §3.6).</summary>
    public const int MaxPostingAttempts = 3;

    public static readonly SortMap<Invoice> Sorts = new SortMap<Invoice>("-invoiceDate,-id", i => i.Id)
        .Add("invoiceDate", i => i.InvoiceDate)
        .Add("documentNo", i => i.DocumentNo)
        .Add("grandTotal", i => i.GrandTotal)
        .Add("status", i => i.Status)
        .Add("createdAt", i => i.CreatedAt);

    private sealed record PostedInvoice(int Id, int CompanyId, IReadOnlyList<int> StockIds, int AccountId, string? DocumentNo, decimal GrandTotal);

    public Task<PagedResult<InvoiceListItemDto>> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken = default)
    {
        var q = HistoryInvoices();

        if (query.Type is InvoiceType type)
        {
            q = q.Where(i => i.Type == type);
        }

        if (query.Status is InvoiceStatus status)
        {
            q = q.Where(i => i.Status == status);
        }

        if (query.CurrentAccountId is int accountId)
        {
            q = q.Where(i => i.CurrentAccountId == accountId);
        }

        if (!string.IsNullOrWhiteSpace(query.Series))
        {
            var series = DomainRules.NormalizeCode(query.Series);
            q = q.Where(i => i.Series == series);
        }

        if (query.DateFrom is DateOnly from)
        {
            q = q.Where(i => i.InvoiceDate >= from);
        }

        if (query.DateTo is DateOnly to)
        {
            q = q.Where(i => i.InvoiceDate <= to);
        }

        if (query.Search is { } s)
        {
            var code = DomainRules.NormalizeCode(s);
            q = q.Where(i => (i.DocumentNo != null && i.DocumentNo.Contains(code))
                || i.CurrentAccount!.Code.Contains(code)
                || i.CurrentAccount.Title.Contains(s)
                || (i.Description != null && i.Description.Contains(s)));
        }

        return q.ApplySort(query.Sort, Sorts)
            .Select(InvoiceMappings.ListProjection)
            .ToPagedResultAsync(query.Page, query.PageSize, cancellationToken);
    }

    public async Task<InvoiceDto> GetAsync(int id, CancellationToken cancellationToken = default) =>
        await HistoryInvoices().Where(i => i.Id == id).Select(InvoiceMappings.DetailProjection).FirstOrDefaultAsync(cancellationToken)
        ?? throw NotFoundException.For("Fatura");

    public async Task<InvoiceDto> CreateAsync(InvoiceSaveRequest request, CancellationToken cancellationToken = default)
    {
        var companyId = currentUser.RequireCompanyId();
        var type = request.Type ?? throw ValidationHelpers.Failure("type", "Fatura türü zorunludur.");

        var invoice = new Invoice { CompanyId = companyId, Type = type, Status = InvoiceStatus.Draft };
        await ApplyAsync(invoice, request, cancellationToken);

        db.Invoices.Add(invoice);
        await db.SaveOrConflictAsync(errors, _ => new ConflictException(ErrorCodes.ConcurrencyConflict, PersistenceExtensions.ConcurrencyMessage), cancellationToken);
        return await GetAsync(invoice.Id, cancellationToken);
    }

    public async Task<InvoiceDto> UpdateAsync(int id, InvoiceSaveRequest request, CancellationToken cancellationToken = default)
    {
        var rowVersion = ValidationHelpers.ParseRowVersion(request.RowVersion);
        var invoice = await db.Invoices.ForTenant(currentUser).Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken) ?? throw NotFoundException.For("Fatura");
        invoice.EnsureDraft();
        db.UseClientRowVersion(invoice, rowVersion);

        await ApplyAsync(invoice, request, cancellationToken);

        // Always touch the header so the rowversion guard applies even when only lines changed.
        invoice.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveOrConflictAsync(errors, _ => new ConflictException(ErrorCodes.ConcurrencyConflict, PersistenceExtensions.ConcurrencyMessage), cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Invoices.ForTenant(currentUser).Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken) ?? throw NotFoundException.For("Fatura");
        invoice.EnsureDraft();

        db.Invoices.Remove(invoice); // hard delete, lines cascade
        await db.SaveOrConflictAsync(errors, _ => new ConflictException(ErrorCodes.ConcurrencyConflict, PersistenceExtensions.ConcurrencyMessage), cancellationToken);
    }

    public async Task<InvoiceDto> ApproveAsync(int id, InvoiceApproveRequest request, CancellationToken cancellationToken = default)
    {
        var clientRowVersion = ValidationHelpers.ParseRowVersion(request.RowVersion);
        var posted = await PostAsync(id, clientRowVersion, async (invoice, stocks, account, ct) =>
        {
            var max = await db.Invoices
                .Where(i => i.CompanyId == invoice.CompanyId && i.Series == invoice.Series)
                .MaxAsync(i => i.Number, ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            return InvoicePosting.Approve(invoice, stocks, account, (max ?? 0) + 1, now, currentUser.UserId, inventoryOptions.Value.AllowNegativeStock);
        }, cancellationToken);

        logger.LogInformation(
            "Invoice {InvoiceId} approved as {DocumentNo} (GrandTotal {GrandTotal}) by user {UserId}",
            posted.Id, posted.DocumentNo, posted.GrandTotal, currentUser.UserId);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<InvoiceDto> CancelAsync(int id, InvoiceCancelRequest request, CancellationToken cancellationToken = default)
    {
        var clientRowVersion = ValidationHelpers.ParseRowVersion(request.RowVersion);
        var reason = request.Reason?.Trim() ?? string.Empty;
        var posted = await PostAsync(id, clientRowVersion, async (invoice, stocks, account, ct) =>
        {
            var hasAccountTrans = await db.ActTrans.AnyAsync(a => a.InvoiceId == invoice.Id && !a.IsReversal, ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            return InvoicePosting.Cancel(invoice, stocks, account, hasAccountTrans, reason, now, currentUser.UserId, inventoryOptions.Value.AllowNegativeStock);
        }, cancellationToken);

        logger.LogInformation(
            "Invoice {InvoiceId} {DocumentNo} cancelled (GrandTotal {GrandTotal}) by user {UserId}. Reason: {Reason}",
            posted.Id, posted.DocumentNo, posted.GrandTotal, currentUser.UserId, reason);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>
    /// Shared approve/cancel pipeline (CONTRACT §3.6, DB_NOTES §2.2–2.3): up to 3 attempts; each attempt clears the
    /// change tracker, runs inside the execution strategy with a ReadCommitted transaction, reloads invoice/stocks/
    /// account (tracked), applies <paramref name="post"/>, saves ONCE and commits. The client rowVersion is checked on
    /// the first attempt only. Cache invalidation happens after commit.
    /// </summary>
    private async Task<PostedInvoice> PostAsync(
        int id,
        byte[] clientRowVersion,
        Func<Invoice, IReadOnlyDictionary<int, Stock>, CurrentAccount, CancellationToken, Task<PostingResult>> post,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var currentAttempt = attempt;
            try
            {
                var strategy = db.Database.CreateExecutionStrategy();
                var posted = await strategy.ExecuteAsync(
                    async ct =>
                    {
                        db.ChangeTracker.Clear();
                        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

                        var invoice = await db.Invoices.ForTenant(currentUser).Include(i => i.Lines)
                            .FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw NotFoundException.For("Fatura");
                        if (currentAttempt == 1 && !invoice.RowVersion.AsSpan().SequenceEqual(clientRowVersion))
                        {
                            throw new ConflictException(ErrorCodes.ConcurrencyConflict, PersistenceExtensions.ConcurrencyMessage);
                        }

                        var stockIds = invoice.Lines.Select(l => l.StockId).Distinct().ToList();
                        var stocks = await db.Stocks.IgnoreQueryFilters([QueryFilterNames.SoftDelete])
                            .Where(s => stockIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
                        var account = await db.CurrentAccounts.IgnoreQueryFilters([QueryFilterNames.SoftDelete])
                            .FirstAsync(a => a.Id == invoice.CurrentAccountId, ct);

                        var result = await post(invoice, stocks, account, ct);
                        db.StockTrans.AddRange(result.StockTrans);
                        if (result.ActTrans is not null)
                        {
                            db.ActTrans.Add(result.ActTrans);
                        }

                        await db.SaveChangesAsync(ct); // ONE SaveChanges: ledgers + balances + invoice status/number
                        await tx.CommitAsync(ct);
                        return new PostedInvoice(invoice.Id, invoice.CompanyId, stockIds, account.Id, invoice.DocumentNo, invoice.GrandTotal);
                    },
                    cancellationToken);

                db.ChangeTracker.Clear();
                await cacheInvalidator.InvoicePostedAsync(posted.CompanyId, posted.StockIds, posted.AccountId, cancellationToken);
                return posted;
            }
            catch (DbUpdateException ex) when (IsRetryable(ex))
            {
                db.ChangeTracker.Clear();
                if (attempt >= MaxPostingAttempts)
                {
                    logger.LogWarning(ex, "Invoice {InvoiceId} posting failed after {Attempts} attempts", id, attempt);
                    throw new ConflictException(ErrorCodes.ConcurrencyConflict, PersistenceExtensions.ConcurrencyMessage);
                }

                logger.LogInformation("Invoice {InvoiceId} posting conflict on attempt {Attempt}; retrying", id, attempt);
            }
        }
    }

    private bool IsRetryable(DbUpdateException ex) => ex is DbUpdateConcurrencyException || errors.IsUniqueViolation(ex, out _);

    /// <summary>Invoices of the tenant incl. those referencing soft-deleted stocks/accounts (DB_NOTES §2.5).</summary>
    private IQueryable<Invoice> HistoryInvoices() =>
        db.Invoices.AsNoTracking().IgnoreQueryFilters([QueryFilterNames.SoftDelete]).ForTenant(currentUser);

    /// <summary>Applies header + lines (replacing all lines) and validates references (CONTRACT §3.3, §3.8).</summary>
    private async Task ApplyAsync(Invoice invoice, InvoiceSaveRequest request, CancellationToken cancellationToken)
    {
        var account = await db.CurrentAccounts.FirstOrDefaultAsync(
            a => a.Id == request.CurrentAccountId && a.CompanyId == invoice.CompanyId, cancellationToken)
            ?? throw new BusinessRuleException(ErrorCodes.InvalidReference, "Cari hesap bulunamadı veya firmaya ait değil.");

        var stockIds = request.Lines.Select(l => l.StockId).Distinct().ToList();
        var stocks = await db.Stocks.Where(s => stockIds.Contains(s.Id) && s.CompanyId == invoice.CompanyId)
            .ToDictionaryAsync(s => s.Id, cancellationToken);
        var missing = stockIds.Where(id => !stocks.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidReference, "Stok bulunamadı veya firmaya ait değil: " + string.Join(", ", missing));
        }

        invoice.Series = string.IsNullOrWhiteSpace(request.Series)
            ? Invoice.DefaultSeriesFor(invoice.Type)
            : DomainRules.NormalizeCode(request.Series);
        invoice.InvoiceDate = request.InvoiceDate ?? throw ValidationHelpers.Failure("invoiceDate", "Fatura tarihi zorunludur.");
        invoice.DueDate = request.DueDate;
        invoice.CurrentAccountId = account.Id;
        invoice.Description = ValidationHelpers.Clean(request.Description);

        // Reuse existing line rows by position to avoid (InvoiceId, LineNo) unique conflicts; drop surplus rows.
        invoice.Lines.Sort((a, b) => a.LineNo.CompareTo(b.LineNo));
        for (var i = 0; i < request.Lines.Count; i++)
        {
            InvoiceLine line;
            if (i < invoice.Lines.Count)
            {
                line = invoice.Lines[i];
            }
            else
            {
                line = new InvoiceLine();
                invoice.Lines.Add(line);
            }

            var r = request.Lines[i];
            line.StockId = r.StockId;
            line.Description = ValidationHelpers.Clean(r.Description);
            line.Quantity = r.Quantity;
            line.UnitPrice = r.UnitPrice;
            line.VatRate = r.VatRate;
        }

        if (invoice.Lines.Count > request.Lines.Count)
        {
            var surplus = invoice.Lines.Skip(request.Lines.Count).ToList();
            foreach (var line in surplus)
            {
                invoice.Lines.Remove(line);
                if (line.Id != 0)
                {
                    db.InvoiceLines.Remove(line);
                }
            }
        }

        invoice.RenumberLines();
        invoice.RecalculateTotals();

        InvoicePosting.EnsureAccountUsable(invoice, account);
        InvoicePosting.EnsureStocksUsable(invoice, stocks);
    }
}
