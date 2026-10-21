using StockAccount.Domain.Common;

namespace StockAccount.Domain.Entities;

/// <summary>Tenant root (table Companies).</summary>
public class Company : AuditableEntity, ISoftDelete, IHasRowVersion
{
    public string Name { get; set; } = string.Empty;
    public string TaxNumber { get; set; } = string.Empty;
    public string? TaxOffice { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
