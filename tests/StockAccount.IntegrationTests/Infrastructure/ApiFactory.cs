using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using StockAccount.Infrastructure.Persistence;

namespace StockAccount.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API (Program) in-memory against a throw-away SQL Server database (CONTRACT §11.2):
/// <c>TEST_SQL_CONNECTION</c> or <c>.\SQLEXPRESS</c> with database <c>StockAccountTests_{guid}</c>, migrated + seeded on
/// startup by the API itself. In-memory distributed cache, background reconciliation disabled.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> overrides;

    private readonly Action<IServiceCollection>? configureServices;

    public ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? overrides = null, Action<IServiceCollection>? configureServices = null)
    {
        ConnectionString = connectionString;
        this.overrides = overrides ?? new Dictionary<string, string?>();
        this.configureServices = configureServices;
    }

    public string ConnectionString { get; }

    public static string NewConnectionString()
    {
        var template = Environment.GetEnvironmentVariable("TEST_SQL_CONNECTION");
        var dbName = $"StockAccountTests_{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(template))
        {
            return $"Server=.\\SQLEXPRESS;Database={dbName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False";
        }

        // Allow "{guid}" / "{db}" placeholders, otherwise replace the Database part.
        if (template.Contains("{guid}", StringComparison.OrdinalIgnoreCase))
        {
            return template.Replace("{guid}", Guid.NewGuid().ToString("N"), StringComparison.OrdinalIgnoreCase);
        }

        var b = new SqlConnectionStringBuilder(template) { InitialCatalog = dbName };
        return b.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = ConnectionString,
            ["ConnectionStrings:Redis"] = "",
            ["Cache:Enabled"] = "true",
            ["Cache:UseRedis"] = "false",
            ["Reconciliation:Enabled"] = "false",
            ["Database:MigrateOnStartup"] = "true",
            ["Database:Seed"] = "true",
            ["Inventory:AllowNegativeStock"] = "false",
            ["Serilog:WriteTo:1:Args:path"] = Path.Combine(AppContext.BaseDirectory, "logs", "it-.log"),
            ["Serilog:MinimumLevel:Default"] = "Warning",
        };
        foreach (var (k, v) in overrides)
        {
            settings[k] = v;
        }

        foreach (var (k, v) in settings)
        {
            builder.UseSetting(k, v);
        }

        if (configureServices is not null)
        {
            builder.ConfigureTestServices(configureServices);
        }
    }

    /// <summary>Runs raw SQL against the test database (e.g. tampering Stocks.Balance for reconciliation tests).</summary>
    public async Task<int> ExecuteSqlAsync(string sql, params SqlParameter[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SET QUOTED_IDENTIFIER ON; " + sql;
        cmd.Parameters.AddRange(parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        var result = await cmd.ExecuteScalarAsync();
        return result switch
        {
            null or DBNull => default,
            T typed => typed,
            _ => (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, params SqlParameter[] parameters)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Name of the database the API actually uses (sanity check that the override was applied).</summary>
    public string ActualDatabaseName()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new SqlConnectionStringBuilder(Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetConnectionString(db.Database)).InitialCatalog;
    }

    public static async Task DropDatabaseAsync(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var dbName = b.InitialCatalog;
        if (!dbName.StartsWith("StockAccountTests_", StringComparison.Ordinal) && !dbName.StartsWith("StockAccountDb_E2E", StringComparison.Ordinal))
        {
            return; // never drop anything that is not a test database
        }

        SqlConnection.ClearAllPools();
        b.InitialCatalog = "master";
        await using var conn = new SqlConnection(b.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID(N'{dbName}') IS NOT NULL BEGIN ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]; END";
        await cmd.ExecuteNonQueryAsync();
    }
}
