using StockAccount.Application.Common.Exceptions;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Invoices;

/// <summary>Ledger rows produced by an approval or cancellation (ActTrans is null when GrandTotal is 0).</summary>
public sealed record PostingResult(IReadOnlyList<StockTrans> StockTrans, ActTrans? ActTrans);

/// <summary>
/// Pure approval/cancellation rules (CONTRACT §3.2–3.5). Works on already-loaded, tracked entities: validates,
/// mutates invoice status and stored balances, and returns the ledger rows to insert. No I/O, so it is unit-testable.
/// </summary>
public static class InvoicePosting
{
    /// <summary>Approves a Draft invoice with the given number. Balances: stock ∓ qty, account ± GrandTotal.</summary>
    public static PostingResult Approve(
        Invoice invoice,
        IReadOnlyDictionary<int, Stock> stocks,
        CurrentAccount account,
        int number,
        DateTime nowUtc,
        int? userId,
        bool allowNegativeStock)
    {
        invoice.EnsureDraft();
        if (invoice.Lines.Count == 0)
        {
            throw new BusinessRuleException(ErrorCodes.InvoiceHasNoLines, "Satırı olmayan fatura onaylanamaz.");
        }

        EnsureAccountUsable(invoice, account);
        EnsureStocksUsable(invoice, stocks);

        var isSales = invoice.Type == InvoiceType.Sales;
        if (isSales && !allowNegativeStock)
        {
            EnsureSufficientStock(invoice, stocks);
        }

        invoice.RecalculateTotals();
        invoice.Number = number;
        invoice.DocumentNo = Invoice.FormatDocumentNo(invoice.Series, number);
        invoice.Status = InvoiceStatus.Approved;
        invoice.ApprovedAt = nowUtc;
        invoice.ApprovedByUserId = userId;

        var label = isSales ? "Satış faturası" : "Alış faturası";
        var description = $"{invoice.DocumentNo} - {label}";
        var stockDirection = isSales ? StockTransDirection.Out : StockTransDirection.In;

        var stockRows = new List<StockTrans>(invoice.Lines.Count);
        foreach (var line in invoice.Lines.OrderBy(l => l.LineNo))
        {
            var row = NewStockTrans(invoice, line, stockDirection, invoice.InvoiceDate, isReversal: false, description, userId);
            stockRows.Add(row);
            stocks[line.StockId].Balance += row.SignedQuantity;
        }

        ActTrans? actRow = null;
        if (invoice.GrandTotal > 0)
        {
            var direction = isSales ? ActTransDirection.Debit : ActTransDirection.Credit;
            actRow = NewActTrans(invoice, direction, invoice.InvoiceDate, isReversal: false, description, userId);
            account.Balance += actRow.SignedAmount;
        }

        return new PostingResult(stockRows, actRow);
    }

    /// <summary>
    /// Cancels an Approved invoice: writes the exact opposite rows (IsReversal = true, TransDate = UTC today) and
    /// reverses balances. <paramref name="hasAccountTrans"/> tells whether the approval wrote an ActTrans row.
    /// </summary>
    public static PostingResult Cancel(
        Invoice invoice,
        IReadOnlyDictionary<int, Stock> stocks,
        CurrentAccount account,
        bool hasAccountTrans,
        string reason,
        DateTime nowUtc,
        int? userId,
        bool allowNegativeStock)
    {
        if (invoice.Status != InvoiceStatus.Approved)
        {
            throw new ConflictException(ErrorCodes.InvoiceNotApproved, "Yalnızca onaylanmış faturalar iptal edilebilir.");
        }

        var isSales = invoice.Type == InvoiceType.Sales;
        if (!isSales && !allowNegativeStock)
        {
            // Reversing a purchase takes the goods out again.
            EnsureSufficientStock(invoice, stocks);
        }

        var today = DateOnly.FromDateTime(nowUtc);
        var description = $"{invoice.DocumentNo} - İptal";
        var stockDirection = isSales ? StockTransDirection.In : StockTransDirection.Out;

        var stockRows = new List<StockTrans>(invoice.Lines.Count);
        foreach (var line in invoice.Lines.OrderBy(l => l.LineNo))
        {
            var row = NewStockTrans(invoice, line, stockDirection, today, isReversal: true, description, userId);
            stockRows.Add(row);
            stocks[line.StockId].Balance += row.SignedQuantity;
        }

        ActTrans? actRow = null;
        if (hasAccountTrans)
        {
            var direction = isSales ? ActTransDirection.Credit : ActTransDirection.Debit;
            actRow = NewActTrans(invoice, direction, today, isReversal: true, description, userId);
            account.Balance += actRow.SignedAmount;
        }

        invoice.Status = InvoiceStatus.Cancelled;
        invoice.CancelledAt = nowUtc;
        invoice.CancelledByUserId = userId;
        invoice.CancelReason = reason.Trim();

        return new PostingResult(stockRows, actRow);
    }

    /// <summary>Stocks whose balance would go below zero when the invoice quantities are taken out (codes, ordered).</summary>
    public static IReadOnlyList<string> FindInsufficientStocks(Invoice invoice, IReadOnlyDictionary<int, Stock> stocks) =>
        invoice.Lines
            .GroupBy(l => l.StockId)
            .Where(g => stocks[g.Key].Balance - g.Sum(l => l.Quantity) < 0)
            .Select(g => stocks[g.Key].Code)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

    private static void EnsureSufficientStock(Invoice invoice, IReadOnlyDictionary<int, Stock> stocks)
    {
        var insufficient = FindInsufficientStocks(invoice, stocks);
        if (insufficient.Count > 0)
        {
            throw new BusinessRuleException(ErrorCodes.InsufficientStock, "Yetersiz stok: " + string.Join(", ", insufficient));
        }
    }

    internal static void EnsureAccountUsable(Invoice invoice, CurrentAccount account)
    {
        if (!account.IsActive || account.IsDeleted)
        {
            throw new BusinessRuleException(ErrorCodes.InactiveAccount, $"Cari hesap aktif değil: {account.Code}");
        }

        if (!account.IsCompatibleWith(invoice.Type))
        {
            throw new BusinessRuleException(ErrorCodes.AccountTypeMismatch, AccountTypeMismatchMessage(invoice.Type));
        }
    }

    internal static void EnsureStocksUsable(Invoice invoice, IReadOnlyDictionary<int, Stock> stocks)
    {
        var inactive = invoice.Lines
            .Select(l => stocks.TryGetValue(l.StockId, out var s) ? s : null)
            .Where(s => s is null || !s.IsActive || s.IsDeleted)
            .Select(s => s?.Code ?? "?")
            .Distinct()
            .ToList();
        if (inactive.Count > 0)
        {
            throw new BusinessRuleException(ErrorCodes.InactiveStock, "Aktif olmayan stok: " + string.Join(", ", inactive));
        }
    }

    internal static string AccountTypeMismatchMessage(InvoiceType type) => type == InvoiceType.Sales
        ? "Satış faturası yalnızca Müşteri veya Müşteri/Tedarikçi tipindeki carilere kesilebilir."
        : "Alış faturası yalnızca Tedarikçi veya Müşteri/Tedarikçi tipindeki carilerden alınabilir.";

    private static StockTrans NewStockTrans(
        Invoice invoice, InvoiceLine line, StockTransDirection direction, DateOnly date, bool isReversal, string description, int? userId) => new()
        {
            CompanyId = invoice.CompanyId,
            StockId = line.StockId,
            InvoiceId = invoice.Id,
            InvoiceLineId = line.Id == 0 ? null : line.Id,
            TransDate = date,
            Direction = direction,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            Amount = line.LineNet,
            IsReversal = isReversal,
            Description = Truncate(description, 250),
            CreatedByUserId = userId,
        };

    private static ActTrans NewActTrans(
        Invoice invoice, ActTransDirection direction, DateOnly date, bool isReversal, string description, int? userId) => new()
        {
            CompanyId = invoice.CompanyId,
            CurrentAccountId = invoice.CurrentAccountId,
            InvoiceId = invoice.Id,
            TransDate = date,
            Direction = direction,
            Amount = invoice.GrandTotal,
            IsReversal = isReversal,
            Description = Truncate(description, 250),
            CreatedByUserId = userId,
        };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
