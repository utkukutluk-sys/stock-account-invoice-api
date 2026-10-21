using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Infrastructure.Persistence.Interceptors;

namespace StockAccount.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Registers AppDbContext (SQL Server, retry on transient failures), the audit interceptor and
    /// <see cref="IApplicationDbContext"/>. Identity is NOT registered here: Backend adds
    /// <c>AddIdentityCore&lt;AppUser&gt;().AddRoles&lt;AppRole&gt;().AddEntityFrameworkStores&lt;AppDbContext&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddScoped<AuditSaveChangesInterceptor>();

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            });
            options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
            ConfigureCommon(options);
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        return services;
    }

    /// <summary>Options shared by runtime and design-time contexts.</summary>
    internal static void ConfigureCommon(DbContextOptionsBuilder options)
    {
        // Required navigations (InvoiceLine->Stock, Invoice->CurrentAccount, ledgers->Stock/Account) point to
        // soft-deletable entities. This is by design; see docs/DB_NOTES.md section 3 for how to query history.
        options.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }
}
