using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Security;
using StockAccount.Application.Features.Auth;
using StockAccount.Application.Features.Companies;
using StockAccount.Application.Features.CurrentAccounts;
using StockAccount.Application.Features.Dashboard;
using StockAccount.Application.Features.Invoices;
using StockAccount.Application.Features.Portal;
using StockAccount.Application.Features.Reconciliation;
using StockAccount.Application.Features.Stocks;
using StockAccount.Application.Features.Transactions;
using StockAccount.Application.Features.Users;

namespace StockAccount.Application;

public static class DependencyInjection
{
    /// <summary>Application services, validators and option bindings.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));
        services.Configure<InventoryOptions>(configuration.GetSection(InventoryOptions.SectionName));
        services.Configure<ReconciliationOptions>(configuration.GetSection(ReconciliationOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<DependencyInjectionMarker>(ServiceLifetime.Singleton);

        services.AddScoped<ICacheInvalidator, CacheInvalidator>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ICompanyService, CompanyService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<ICurrentAccountService, CurrentAccountService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IPortalService, PortalService>();
        return services;
    }

    private sealed class DependencyInjectionMarker;
}
