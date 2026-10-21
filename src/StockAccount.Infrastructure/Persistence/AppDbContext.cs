using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Identity;
using StockAccount.Domain.Entities;
using StockAccount.Infrastructure.Persistence.Views;

namespace StockAccount.Infrastructure.Persistence;

/// <summary>
/// EF Core context: Identity tables (int keys) + business tables. Soft-delete filters are named
/// (<see cref="QueryFilterNames.SoftDelete"/>). There is intentionally no global tenant filter (CONTRACT §3.8).
/// </summary>
public class AppDbContext : IdentityDbContext<AppUser, AppRole, int>, IApplicationDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    /// <summary>Case-insensitive, accent-sensitive Turkish collation (correct İ/ı, Ç, Ş, Ğ handling).</summary>
    public const string DatabaseCollation = "Turkish_CI_AS";

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<CurrentAccount> CurrentAccounts => Set<CurrentAccount>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<StockTrans> StockTrans => Set<StockTrans>();
    public DbSet<ActTrans> ActTrans => Set<ActTrans>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>Read-only reconciliation view (all companies, incl. soft-deleted stocks). See docs/DB_NOTES.md.</summary>
    public DbSet<StockBalanceCheck> StockBalanceChecks => Set<StockBalanceCheck>();

    /// <summary>Read-only reconciliation view (all companies, incl. soft-deleted accounts). See docs/DB_NOTES.md.</summary>
    public DbSet<AccountBalanceCheck> AccountBalanceChecks => Set<AccountBalanceCheck>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Pin the database collation so local SQLEXPRESS (Turkish server default) and the Docker image
        // (SQL_Latin1 default) behave the same for search, sorting and unique indexes. Applied on CREATE DATABASE.
        builder.UseCollation(DatabaseCollation);

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Safety net: any decimal without explicit precision gets money precision instead of EF's silent (18,2) warning.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2");
    }
}
