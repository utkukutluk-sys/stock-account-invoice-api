using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAccount.Application.Common.Identity;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotent seed (CONTRACT §9). Every item is looked up by its unique key first; running twice creates
/// no duplicates. The opening purchase invoice is created as <b>Draft</b> only if the demo company has no
/// invoices; Backend's startup approves it through IInvoiceService (match on
/// <see cref="DomainRules.SeedOpeningInvoiceDescription"/>).
/// Users are created through <see cref="UserManager{TUser}"/> (password hashing/validation) and are skipped
/// with a warning if Identity is not registered.
/// </summary>
public static class DataSeeder
{
    public const string AdminEmail = "admin@stockaccount.local";
    public const string AdminPassword = "Admin123!";
    public const string OperatorEmail = "demo@stockaccount.local";
    public const string OperatorPassword = "Demo123!";
    public const string PortalEmail = "ahmet@stockaccount.local";
    public const string PortalPassword = "Ahmet123!";
    public const string DemoCompanyTaxNumber = "1234567890";

    private static readonly (string Code, string Name, StockUnit Unit, decimal Vat, decimal Price)[] SeedStocks =
    [
        ("ELMA", "Elma", StockUnit.Kg, 1m, 50.00m),
        ("ARMUT", "Armut", StockUnit.Kg, 1m, 60.00m),
        ("SUT", "Süt 1L", StockUnit.Liter, 1m, 35.00m),
        ("EKMEK", "Ekmek", StockUnit.Piece, 1m, 15.00m),
        ("DETERJAN", "Deterjan 3kg", StockUnit.Piece, 20m, 250.00m),
    ];

    private static readonly (string Code, string Title, CurrentAccountType Type, string? City, string? TaxNumber)[] SeedAccounts =
    [
        ("C001", "Ahmet Yılmaz", CurrentAccountType.Customer, "İstanbul", null),
        ("C002", "Ayşe Demir", CurrentAccountType.Customer, "Ankara", null),
        ("S001", "Bereket Tarım Ltd. Şti.", CurrentAccountType.Supplier, null, "9876543210"),
        ("B001", "Karma Ticaret A.Ş.", CurrentAccountType.Both, null, null),
    ];

    private static readonly (string StockCode, decimal Quantity, decimal UnitPrice)[] OpeningLines =
    [
        ("ELMA", 100m, 30m),
        ("ARMUT", 80m, 40m),
        ("SUT", 200m, 20m),
        ("EKMEK", 150m, 8m),
        ("DETERJAN", 40m, 150m),
    ];

    /// <summary>Runs the seed in its own DI scope (safe to pass the root provider).</summary>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(DataSeeder).FullName!) ?? NullLogger.Instance;

        await SeedRolesAsync(db, cancellationToken);
        var company = await SeedCompanyAsync(db, cancellationToken);
        var stocks = await SeedStocksAsync(db, company.Id, cancellationToken);
        var accounts = await SeedAccountsAsync(db, company.Id, cancellationToken);

        var userManager = sp.GetService<UserManager<AppUser>>();
        if (userManager is null)
        {
            logger.LogWarning("UserManager<AppUser> is not registered; seed users were skipped. Register Identity before calling DataSeeder.");
        }
        else
        {
            await SeedUserAsync(userManager, logger, AdminEmail, AdminPassword, "Sistem Yöneticisi", Roles.Admin, null, null);
            await SeedUserAsync(userManager, logger, OperatorEmail, OperatorPassword, "Demo Kullanıcı", Roles.User, company.Id, null);
            await SeedUserAsync(userManager, logger, PortalEmail, PortalPassword, "Ahmet Yılmaz", Roles.User, company.Id, accounts["C001"].Id);
        }

        await SeedOpeningInvoiceAsync(db, company.Id, stocks, accounts["S001"], logger, cancellationToken);
    }

    public static async Task SeedRolesAsync(AppDbContext db, CancellationToken ct)
    {
        foreach (var roleName in Roles.All)
        {
            var normalized = roleName.ToUpperInvariant();
            if (!await db.Roles.AnyAsync(r => r.NormalizedName == normalized, ct))
            {
                db.Roles.Add(new AppRole(roleName) { NormalizedName = normalized, ConcurrencyStamp = Guid.NewGuid().ToString() });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<Company> SeedCompanyAsync(AppDbContext db, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.TaxNumber == DemoCompanyTaxNumber, ct);
        if (company is null)
        {
            company = new Company
            {
                Name = "Demo Gıda A.Ş.",
                TaxNumber = DemoCompanyTaxNumber,
                TaxOffice = "Kadıköy",
                City = "İstanbul",
                IsActive = true,
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync(ct);
        }

        return company;
    }

    private static async Task<Dictionary<string, Stock>> SeedStocksAsync(AppDbContext db, int companyId, CancellationToken ct)
    {
        var existing = await db.Stocks.Where(s => s.CompanyId == companyId).ToDictionaryAsync(s => s.Code, ct);
        foreach (var (code, name, unit, vat, price) in SeedStocks)
        {
            if (!existing.ContainsKey(code))
            {
                var stock = new Stock { CompanyId = companyId, Code = code, Name = name, Unit = unit, VatRate = vat, UnitPrice = price, IsActive = true };
                db.Stocks.Add(stock);
                existing[code] = stock;
            }
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    private static async Task<Dictionary<string, CurrentAccount>> SeedAccountsAsync(AppDbContext db, int companyId, CancellationToken ct)
    {
        var existing = await db.CurrentAccounts.Where(a => a.CompanyId == companyId).ToDictionaryAsync(a => a.Code, ct);
        foreach (var (code, title, type, city, taxNumber) in SeedAccounts)
        {
            if (!existing.ContainsKey(code))
            {
                var account = new CurrentAccount { CompanyId = companyId, Code = code, Title = title, Type = type, City = city, TaxNumber = taxNumber, IsActive = true };
                db.CurrentAccounts.Add(account);
                existing[code] = account;
            }
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    private static async Task SeedUserAsync(
        UserManager<AppUser> userManager, ILogger logger, string email, string password, string fullName, string role, int? companyId, int? currentAccountId)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return;
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName,
            CompanyId = companyId,
            CurrentAccountId = currentAccountId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new InvalidOperationException($"Seeding user {email} failed: {string.Join("; ", created.Errors.Select(e => e.Description))}");
        }

        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException($"Assigning role {role} to {email} failed: {string.Join("; ", roleResult.Errors.Select(e => e.Description))}");
        }

        logger.LogInformation("Seeded user {Email} with role {Role}", email, role);
    }

    private static async Task SeedOpeningInvoiceAsync(
        AppDbContext db, int companyId, IReadOnlyDictionary<string, Stock> stocks, CurrentAccount supplier, ILogger logger, CancellationToken ct)
    {
        if (await db.Invoices.AnyAsync(i => i.CompanyId == companyId, ct))
        {
            return;
        }

        var invoice = new Invoice
        {
            CompanyId = companyId,
            Type = InvoiceType.Purchase,
            Status = InvoiceStatus.Draft,
            Series = DomainRules.DefaultPurchaseSeries,
            InvoiceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CurrentAccountId = supplier.Id,
            Description = DomainRules.SeedOpeningInvoiceDescription,
        };

        foreach (var (stockCode, quantity, unitPrice) in OpeningLines)
        {
            var stock = stocks[stockCode];
            invoice.Lines.Add(new InvoiceLine { StockId = stock.Id, Quantity = quantity, UnitPrice = unitPrice, VatRate = stock.VatRate });
        }

        invoice.RenumberLines();
        invoice.RecalculateTotals();

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded Draft opening purchase invoice {InvoiceId} (GrandTotal {GrandTotal})", invoice.Id, invoice.GrandTotal);
    }
}
