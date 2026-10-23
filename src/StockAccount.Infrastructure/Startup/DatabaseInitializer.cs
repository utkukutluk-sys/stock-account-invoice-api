using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockAccount.Application.Features.Invoices;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Domain.Common;
using StockAccount.Domain.Enums;
using StockAccount.Infrastructure.Persistence;
using StockAccount.Infrastructure.Persistence.Seed;

namespace StockAccount.Infrastructure.Startup;

/// <summary>
/// Startup DB work (BE-9): migrate with retry (Database:MigrateOnStartup), seed (Database:Seed) and approve the seeded
/// Draft opening invoice through the real <see cref="IInvoiceService"/> so ledgers and balances are consistent.
/// </summary>
public static class DatabaseInitializer
{
    public const int MigrateAttempts = 10;
    public static readonly TimeSpan MigrateRetryDelay = TimeSpan.FromSeconds(5);

    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (configuration.GetValue("Database:MigrateOnStartup", false))
        {
            await MigrateWithRetryAsync(services, configuration, logger, cancellationToken);
        }
        else if (configuration.GetValue("Database:VerifySchemaOnStartup", true))
        {
            await using var scope = services.CreateAsyncScope();
            await EnsureSchemaNotNewerAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), cancellationToken);
        }

        if (configuration.GetValue("Database:Seed", false))
        {
            await DataSeeder.SeedAsync(services, cancellationToken);
            await ApproveOpeningInvoicesAsync(services, logger, cancellationToken);
        }
    }

    /// <summary>
    /// Migrator mode (<c>--migrate-only</c>, compose service <c>db-migrate</c> running with the privileged connection):
    /// schema guard, migrate, database options and the least-privilege app login, then (only when Database:Seed is off) the
    /// one-time admin bootstrap (<see cref="AdminBootstrapper"/>; mandatory when <paramref name="requireAdmin"/>, i.e. Production).
    /// </summary>
    public static async Task MigrateOnlyAsync(
        IServiceProvider services, IConfiguration configuration, ILogger logger, bool requireAdmin = false, CancellationToken cancellationToken = default)
    {
        await MigrateWithRetryAsync(services, configuration, logger, cancellationToken);
        if (!configuration.GetValue("Database:Seed", false))
        {
            await AdminBootstrapper.EnsureAsync(services, configuration, requireAdmin, logger, cancellationToken);
        }
    }

    private static async Task MigrateWithRetryAsync(IServiceProvider services, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await EnsureSchemaNotNewerAsync(db, cancellationToken);
                db.Database.SetCommandTimeout(TimeSpan.FromSeconds(300)); // migrations only; runtime uses Database:CommandTimeoutSeconds
                await db.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Database migrated (attempt {Attempt})", attempt);
                await DatabaseProvisioner.ApplyAsync(db, configuration, logger, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MigrateAttempts && ex is not (OperationCanceledException or DatabaseSchemaMismatchException))
            {
                logger.LogWarning(ex, "Database migration attempt {Attempt}/{Max} failed; retrying in {Delay}", attempt, MigrateAttempts, MigrateRetryDelay);
                await Task.Delay(MigrateRetryDelay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Rollback guard: refuse to run against a database that has migrations this build does not know (i.e. the schema was
    /// upgraded by a newer release). Restore the pre-deploy backup instead (scripts/deploy/rollback.ps1 -RestoreDatabase);
    /// EF down-migrations are not used.
    /// </summary>
    public static async Task EnsureSchemaNotNewerAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return; // database does not exist yet → MigrateAsync creates it
        }

        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Where(m => !known.Contains(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new DatabaseSchemaMismatchException(
                $"Database schema is newer than this API version (unknown applied migrations: {string.Join(", ", unknown)}). "
                + "Roll back with scripts/deploy/rollback.ps1 -RestoreDatabase or deploy a matching version.");
        }
    }

    /// <summary>Approves every Draft invoice whose description is the seed marker (CONTRACT §9). Idempotent.</summary>
    public static async Task ApproveOpeningInvoicesAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var drafts = await db.Invoices.AsNoTracking()
            .Where(i => i.Status == InvoiceStatus.Draft && i.Description == DomainRules.SeedOpeningInvoiceDescription)
            .Select(i => new { i.Id, i.RowVersion })
            .ToListAsync(cancellationToken);

        var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceService>();
        foreach (var draft in drafts)
        {
            var approved = await invoices.ApproveAsync(
                draft.Id, new InvoiceApproveRequest { RowVersion = Convert.ToBase64String(draft.RowVersion) }, cancellationToken);
            logger.LogInformation("Seed opening invoice {InvoiceId} approved as {DocumentNo}", approved.Id, approved.DocumentNo);
        }
    }
}

/// <summary>The database contains migrations unknown to this build (startup aborts, exit code 3).</summary>
public sealed class DatabaseSchemaMismatchException(string message) : Exception(message);
