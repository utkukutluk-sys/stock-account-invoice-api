using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Reconciliation;
using StockAccount.Infrastructure.BackgroundServices;
using StockAccount.Infrastructure.Caching;
using StockAccount.Infrastructure.Data;
using StockAccount.Infrastructure.Identity;
using StockAccount.Infrastructure.Persistence;
using StockAccount.Infrastructure.Reconciliation;

namespace StockAccount.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Persistence (DB agent), Identity (CONTRACT §4.1 password/lockout rules), JWT issuing, cache provider selection,
    /// DB error inspection, reconciliation service + background service.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPersistence(configuration);
        services.AddSingleton<IDatabaseErrorInspector, SqlServerDatabaseErrorInspector>();

        services
            .AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
                o.Password.RequireDigit = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddErrorDescriber<TurkishIdentityErrorDescriber>()
            .AddDefaultTokenProviders();

        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddAppCaching(configuration);

        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddHostedService<BalanceReconciliationService>();

        return services;
    }
}
