using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Abstractions;

namespace StockAccount.Infrastructure.Data;

/// <summary>SQL Server: 2601 (unique index) / 2627 (unique constraint) are unique violations.</summary>
public sealed partial class SqlServerDatabaseErrorInspector : IDatabaseErrorInspector
{
    public bool IsUniqueViolation(DbUpdateException exception, out string? indexName)
    {
        indexName = null;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 } sql)
            {
                var match = IndexNameRegex().Match(sql.Message);
                indexName = match.Success ? match.Groups[1].Value : null;
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("(?:index|constraint) '([^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexNameRegex();
}
