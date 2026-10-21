namespace StockAccount.Domain.Common;

/// <summary>Entity with a UTC creation timestamp (set by the audit interceptor when left default).</summary>
public interface IHasCreatedAt
{
    DateTime CreatedAt { get; set; }
}

/// <summary>Entity that records who created it (StockTrans, ActTrans, AuditableEntity).</summary>
public interface ICreationAudited : IHasCreatedAt
{
    int? CreatedByUserId { get; set; }
}

/// <summary>Entity that records creation and last modification.</summary>
public interface IAuditable : ICreationAudited
{
    DateTime? UpdatedAt { get; set; }
    int? UpdatedByUserId { get; set; }
}
