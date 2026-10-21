namespace StockAccount.Application.Common.Abstractions;

/// <summary>
/// Names of EF Core 10 named global query filters configured by AppDbContext.
/// Use <c>query.IgnoreQueryFilters([QueryFilterNames.SoftDelete])</c> to see soft-deleted rows.
/// </summary>
public static class QueryFilterNames
{
    /// <summary><c>!IsDeleted</c> on Company, Stock, CurrentAccount.</summary>
    public const string SoftDelete = "SoftDelete";
}
