namespace StockAccount.Domain.Common;

/// <summary>Soft-deletable entity. Removing it through EF turns into an update with IsDeleted = true.</summary>
public interface ISoftDelete
{
    bool IsDeleted { get; set; }
    DateTime? DeletedAt { get; set; }
    int? DeletedByUserId { get; set; }
}

/// <summary>Entity owned by a company (tenant).</summary>
public interface ITenantEntity
{
    int CompanyId { get; set; }
}

/// <summary>Entity with a SQL Server rowversion optimistic concurrency token.</summary>
public interface IHasRowVersion
{
    byte[] RowVersion { get; set; }
}
