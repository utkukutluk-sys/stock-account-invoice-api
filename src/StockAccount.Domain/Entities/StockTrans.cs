using StockAccount.Domain.Common;
using StockAccount.Domain.Enums;

namespace StockAccount.Domain.Entities;

/// <summary>Immutable stock ledger row (table StockTrans). Never updated or deleted.</summary>
public class StockTrans : ITenantEntity, ICreationAudited
{
    public long Id { get; set; }

    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    public int StockId { get; set; }
    public Stock? Stock { get; set; }

    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    /// <summary>Informational only, no FK.</summary>
    public int? InvoiceLineId { get; set; }

    public DateOnly TransDate { get; set; }
    public StockTransDirection Direction { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public bool IsReversal { get; set; }
    public string Description { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }

    /// <summary>Signed quantity for balance math: +Quantity for In, −Quantity for Out.</summary>
    public decimal SignedQuantity => Direction == StockTransDirection.In ? Quantity : -Quantity;
}
