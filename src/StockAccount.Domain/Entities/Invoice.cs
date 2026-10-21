using System.Globalization;
using StockAccount.Domain.Common;
using StockAccount.Domain.Enums;

namespace StockAccount.Domain.Entities;

/// <summary>Invoice header (table Invoices). Not soft-deleted: Draft invoices are hard-deleted.</summary>
public class Invoice : AuditableEntity, ITenantEntity, IHasRowVersion
{
    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    public InvoiceType Type { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public string Series { get; set; } = string.Empty;
    public int? Number { get; set; }
    public string? DocumentNo { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }

    public int CurrentAccountId { get; set; }
    public CurrentAccount? CurrentAccount { get; set; }

    public string? Description { get; set; }
    public decimal SubTotal { get; set; }
    public decimal VatTotal { get; set; }
    public decimal GrandTotal { get; set; }

    public DateTime? ApprovedAt { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelReason { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<InvoiceLine> Lines { get; set; } = [];

    /// <summary>Default series when omitted: "SAT" for Sales, "ALS" for Purchase.</summary>
    public static string DefaultSeriesFor(InvoiceType type) =>
        type == InvoiceType.Purchase ? DomainRules.DefaultPurchaseSeries : DomainRules.DefaultSalesSeries;

    /// <summary>DocumentNo = Series + Number:D6, e.g. SAT000001.</summary>
    public static string FormatDocumentNo(string series, int number) =>
        series + number.ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>Calls Calculate on each line; SubTotal = Σ LineNet, VatTotal = Σ VatAmount, GrandTotal = SubTotal + VatTotal.</summary>
    public void RecalculateTotals()
    {
        decimal sub = 0m, vat = 0m;
        foreach (var line in Lines)
        {
            line.Calculate();
            sub += line.LineNet;
            vat += line.VatAmount;
        }

        SubTotal = sub;
        VatTotal = vat;
        GrandTotal = sub + vat;
    }

    /// <summary>Throws DomainException("INVOICE_NOT_DRAFT") unless the invoice is Draft.</summary>
    public void EnsureDraft()
    {
        if (Status != InvoiceStatus.Draft)
        {
            throw new DomainException("INVOICE_NOT_DRAFT", "Yalnızca taslak faturalar değiştirilebilir veya silinebilir.");
        }
    }

    /// <summary>Re-numbers lines 1..n in their current list order.</summary>
    public void RenumberLines()
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].LineNo = i + 1;
        }
    }
}
