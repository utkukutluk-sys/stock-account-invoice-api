using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using StockAccount.Application.Common.Identity;
using StockAccount.Domain.Entities;

namespace StockAccount.Application.Common.Abstractions;

/// <summary>
/// Persistence abstraction used by Application services. Implemented by Infrastructure's AppDbContext.
/// Soft-deleted Companies/Stocks/CurrentAccounts are hidden by global query filters
/// (use <c>IgnoreQueryFilters()</c> only for reconciliation/admin diagnostics).
/// There is NO global tenant filter: apply tenant scoping explicitly (CONTRACT §3.8).
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Company> Companies { get; }
    DbSet<Stock> Stocks { get; }
    DbSet<CurrentAccount> CurrentAccounts { get; }
    DbSet<Invoice> Invoices { get; }
    DbSet<InvoiceLine> InvoiceLines { get; }
    DbSet<StockTrans> StockTrans { get; }
    DbSet<ActTrans> ActTrans { get; }

    DbSet<AppUser> Users { get; }
    DbSet<AppRole> Roles { get; }
    DbSet<IdentityUserRole<int>> UserRoles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }

    /// <summary>Transactions (BeginTransactionAsync), CreateExecutionStrategy, raw parameterized SQL.</summary>
    DatabaseFacade Database { get; }

    ChangeTracker ChangeTracker { get; }

    /// <summary>Needed e.g. to set the client-supplied RowVersion as OriginalValue for optimistic concurrency.</summary>
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
