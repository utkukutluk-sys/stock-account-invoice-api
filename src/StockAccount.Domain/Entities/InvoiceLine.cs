using StockAccount.Domain.Common;

namespace StockAccount.Domain.Entities;

/// <summary>Invoice line (table InvoiceLines).</summary>
public class InvoiceLine
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public int LineNo { get; set; }

    public int StockId { get; set; }
    public Stock? Stock { get; set; }

    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; }
    public decimal LineNet { get; set; }
    public decimal VatAmount { get; set; }
    public decimal LineTotal { get; set; }

    /// <summary>LineNet = round(Qty × UnitPrice, 2); VatAmount = round(LineNet × VatRate / 100, 2); LineTotal = LineNet + VatAmount.</summary>
    public void Calculate()
    {
        LineNet = DomainRules.RoundMoney(Quantity * UnitPrice);
        VatAmount = DomainRules.RoundMoney(LineNet * VatRate / 100m);
        LineTotal = LineNet + VatAmount;
    }
}
