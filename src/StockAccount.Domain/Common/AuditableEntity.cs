namespace StockAccount.Domain.Common;

/// <summary>Base type for audited entities (CONTRACT §2.1). Audit fields are set by the EF SaveChanges interceptor.</summary>
public abstract class AuditableEntity : IAuditable
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }
}
