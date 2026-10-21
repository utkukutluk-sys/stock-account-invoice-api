using Microsoft.EntityFrameworkCore;

namespace StockAccount.Application.Common.Abstractions;

/// <summary>Provider-specific inspection of <see cref="DbUpdateException"/> (implemented in Infrastructure for SQL Server).</summary>
public interface IDatabaseErrorInspector
{
    /// <summary>True for unique index / constraint violations (SQL Server 2601/2627). <paramref name="indexName"/> is best effort.</summary>
    bool IsUniqueViolation(DbUpdateException exception, out string? indexName);
}
