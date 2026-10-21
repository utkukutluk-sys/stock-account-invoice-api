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
            await MigrateWithRetryAsync(services, logger, cancellationToken);
        }

        if (configuration.GetValue("Database:Seed", false))
        {
            await DataSeeder.SeedAsync(services, cancellationToken);
            await ApproveOpeningInvoicesAsync(services, logger, cancellationToken);
        }
    }

    private static async Task MigrateWithRetryAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Database migrated (attempt {Attempt})", attempt);
                return;
            }
            catch (Exception ex) when (attempt < MigrateAttempts && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Database migration attempt {Attempt}/{Max} failed; retrying in {Delay}", attempt, MigrateAttempts, MigrateRetryDelay);
                await Task.Delay(MigrateRetryDelay, cancellationToken);
            }
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
