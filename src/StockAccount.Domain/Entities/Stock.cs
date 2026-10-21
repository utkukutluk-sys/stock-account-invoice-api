using StockAccount.Domain.Common;
using StockAccount.Domain.Enums;

namespace StockAccount.Domain.Entities;

/// <summary>Stock card (table Stocks).</summary>
public class Stock : AuditableEntity, ISoftDelete, ITenantEntity, IHasRowVersion
{
    public int CompanyId { get; set; }
    public Company? Company { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StockUnit Unit { get; set; }
    public string? Barcode { get; set; }
    public decimal VatRate { get; set; } = 20m;
    public decimal UnitPrice { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>System-maintained: Σ In − Σ Out over StockTrans. Never accept from API input.</summary>
    public decimal Balance { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
