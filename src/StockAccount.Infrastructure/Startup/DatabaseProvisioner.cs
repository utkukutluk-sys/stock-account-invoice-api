using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockAccount.Infrastructure.Persistence;

namespace StockAccount.Infrastructure.Startup;

/// <summary>
/// Privileged, idempotent database settings applied by the migration/init step (DB_NOTES §8 / D15). Runs outside any
/// transaction (ALTER DATABASE is not allowed inside one) and only with the privileged connection (sa / migrator):
/// <list type="bullet">
/// <item>AUTO_CLOSE OFF (SQL Server Express creates databases with AUTO_CLOSE ON).</item>
/// <item>Recovery model from <c>Database:RecoveryModel</c> (SIMPLE | FULL | BULK_LOGGED; unset = leave as is).</item>
/// <item>Least-privilege runtime login/user from <c>Database:AppLogin:Name/Password</c>: db_datareader, db_datawriter,
/// EXECUTE. CHECK constraints and the ledger triggers still apply to it (no ALTER/DDL rights).</item>
/// </list>
/// </summary>
public static partial class DatabaseProvisioner
{
    public static readonly string[] RecoveryModels = ["SIMPLE", "FULL", "BULK_LOGGED"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex LoginNameRegex();

    public static async Task ApplyAsync(AppDbContext db, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        await SetDatabaseOptionsAsync(db, configuration.GetValue<string>("Database:RecoveryModel"), logger, cancellationToken);

        var login = configuration.GetValue<string>("Database:AppLogin:Name");
        var password = configuration.GetValue<string>("Database:AppLogin:Password");
        if (!string.IsNullOrWhiteSpace(login))
        {
            await EnsureAppLoginAsync(db, login, password ?? string.Empty, logger, cancellationToken);
        }
    }

    private static async Task SetDatabaseOptionsAsync(AppDbContext db, string? recoveryModel, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "IF CONVERT(int, DATABASEPROPERTYEX(DB_NAME(), 'IsAutoClose')) = 1 EXEC(N'ALTER DATABASE CURRENT SET AUTO_CLOSE OFF WITH NO_WAIT');",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(recoveryModel))
            {
                // Whitelisted keyword → constant SQL text (never interpolated from configuration).
                var model = RecoveryModels.FirstOrDefault(m => string.Equals(m, recoveryModel.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Database:RecoveryModel must be SIMPLE, FULL or BULK_LOGGED");
                var sql = model switch
                {
                    "SIMPLE" => "IF CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Recovery')) <> N'SIMPLE' EXEC(N'ALTER DATABASE CURRENT SET RECOVERY SIMPLE WITH NO_WAIT');",
                    "FULL" => "IF CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Recovery')) <> N'FULL' EXEC(N'ALTER DATABASE CURRENT SET RECOVERY FULL WITH NO_WAIT');",
                    _ => "IF CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Recovery')) <> N'BULK_LOGGED' EXEC(N'ALTER DATABASE CURRENT SET RECOVERY BULK_LOGGED WITH NO_WAIT');",
                };
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }

            var state = await db.Database.SqlQueryRaw<string>(
                "SELECT CONCAT('AUTO_CLOSE=', CONVERT(int, DATABASEPROPERTYEX(DB_NAME(), 'IsAutoClose')), ' RECOVERY=', CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Recovery'))) AS [Value]")
                .SingleAsync(cancellationToken);
            logger.LogInformation("Database options: {DatabaseOptions}", state);
        }
        catch (SqlException ex) when (ex.Number is 5011 or 262 or 300 or 15247)
        {
            // Not allowed for this principal (e.g. a non-owner login): the app keeps working, an operator must set it.
            logger.LogWarning("Database options not applied (no ALTER DATABASE permission): {Error}", ex.Message);
        }
    }

    private static async Task EnsureAppLoginAsync(AppDbContext db, string login, string password, ILogger logger, CancellationToken cancellationToken)
    {
        if (!LoginNameRegex().IsMatch(login) || string.Equals(login, "sa", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Database:AppLogin:Name is invalid (letters, digits, '_', must not be 'sa').");
        }

        if (password.Length is < 8 or > 128)
        {
            throw new InvalidOperationException("Database:AppLogin:Password must be 8-128 characters.");
        }

        // Names/passwords go in as parameters and are quoted with QUOTENAME inside the dynamic DDL (no concatenation of
        // configuration values into SQL text). ALTER LOGIN keeps the password in sync with .env; ALTER USER re-maps a
        // user that became orphaned after a restore.
        const string sql = """
            DECLARE @sql nvarchar(max);
            IF SUSER_ID(@login) IS NULL
                SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N', CHECK_POLICY = ON, DEFAULT_DATABASE = ' + QUOTENAME(DB_NAME());
            ELSE
                SET @sql = N'ALTER LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ' + QUOTENAME(@password, '''');
            EXEC (@sql);
            IF DATABASE_PRINCIPAL_ID(@login) IS NULL
                SET @sql = N'CREATE USER ' + QUOTENAME(@login) + N' FOR LOGIN ' + QUOTENAME(@login);
            ELSE
                SET @sql = N'ALTER USER ' + QUOTENAME(@login) + N' WITH LOGIN = ' + QUOTENAME(@login);
            EXEC (@sql);
            SET @sql = N'ALTER ROLE db_datareader ADD MEMBER ' + QUOTENAME(@login); EXEC (@sql);
            SET @sql = N'ALTER ROLE db_datawriter ADD MEMBER ' + QUOTENAME(@login); EXEC (@sql);
            SET @sql = N'GRANT EXECUTE TO ' + QUOTENAME(@login); EXEC (@sql);
            """;
        await db.Database.ExecuteSqlRawAsync(
            sql,
            [new SqlParameter("@login", login), new SqlParameter("@password", password)],
            cancellationToken);
        logger.LogInformation("Application login {Login} ensured (db_datareader, db_datawriter, EXECUTE)", login);
    }
}
