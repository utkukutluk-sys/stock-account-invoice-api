using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StockAccount.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for <c>dotnet ef</c>. Connection string: env <c>ConnectionStrings__Default</c>
/// if set, else local SQLEXPRESS. Not used at runtime.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string DefaultConnection =
        "Server=.\\SQLEXPRESS;Database=StockAccountDb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = DefaultConnection;
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
        PersistenceServiceCollectionExtensions.ConfigureCommon(options);

        return new AppDbContext(options.Options);
    }
}
