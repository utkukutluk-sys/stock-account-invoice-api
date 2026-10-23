using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StockAccount.Application.Common.Identity;
using StockAccount.Domain.Entities;
using StockAccount.Infrastructure.Persistence;
using StockAccount.Infrastructure.Persistence.Seed;

namespace StockAccount.Infrastructure.Startup;

/// <summary>Bootstrap is required but not (correctly) configured. The message is safe to log (never contains the password).</summary>
public sealed class BootstrapConfigurationException(string message) : Exception(message);

/// <summary>
/// One-time, idempotent creation of the first Admin user (and optionally the first company) for a production database
/// that is not seeded with demo data. Runs in the <c>--migrate-only</c> step (compose service db-migrate).
/// Configuration (env vars or the Bootstrap section): BOOTSTRAP_ADMIN_EMAIL, BOOTSTRAP_ADMIN_PASSWORD,
/// optional BOOTSTRAP_COMPANY_NAME + BOOTSTRAP_COMPANY_TAX_NUMBER.
/// If an Admin already exists nothing is changed (the password is never reset by this step).
/// </summary>
public static class AdminBootstrapper
{
    public enum Outcome
    {
        AdminAlreadyExists,
        AdminCreated,
        Skipped,
    }

    public static async Task<Outcome> EnsureAsync(
        IServiceProvider services, IConfiguration configuration, bool requireAdmin, ILogger logger, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var userManager = sp.GetRequiredService<UserManager<AppUser>>();

        await DataSeeder.SeedRolesAsync(db, cancellationToken);

        var existingAdmins = await userManager.GetUsersInRoleAsync(Roles.Admin);
        if (existingAdmins.Count > 0)
        {
            logger.LogInformation("Bootstrap: an Admin user already exists, nothing to do");
            return Outcome.AdminAlreadyExists;
        }

        var email = Read(configuration, "BOOTSTRAP_ADMIN_EMAIL", "Bootstrap:AdminEmail");
        var password = Read(configuration, "BOOTSTRAP_ADMIN_PASSWORD", "Bootstrap:AdminPassword");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            if (requireAdmin)
            {
                throw new BootstrapConfigurationException(
                    "No Admin user exists and BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD are not set. "
                    + "Set both in .env (scripts/deploy/new-secrets.ps1 -AdminEmail you@example.com) and deploy again.");
            }

            logger.LogWarning("Bootstrap: no Admin user exists and BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD are not set; skipping");
            return Outcome.Skipped;
        }

        email = email.Trim();
        if (!IsValidEmail(email))
        {
            throw new BootstrapConfigurationException("BOOTSTRAP_ADMIN_EMAIL is not a valid e-mail address.");
        }

        var companyName = Read(configuration, "BOOTSTRAP_COMPANY_NAME", "Bootstrap:CompanyName")?.Trim();
        var companyTax = Read(configuration, "BOOTSTRAP_COMPANY_TAX_NUMBER", "Bootstrap:CompanyTaxNumber")?.Trim();
        var wantsCompany = !string.IsNullOrEmpty(companyName);
        if (wantsCompany && (companyTax is null || companyTax.Length is < 10 or > 11 || !companyTax.All(char.IsAsciiDigit)))
        {
            throw new BootstrapConfigurationException("BOOTSTRAP_COMPANY_NAME requires BOOTSTRAP_COMPANY_TAX_NUMBER (10 or 11 digits).");
        }

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = "Sistem Yöneticisi",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };

        // Password policy (length/digit/upper/lower) is enforced by UserManager; the password is never logged.
        var created = await userManager.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            throw new BootstrapConfigurationException(
                "BOOTSTRAP_ADMIN_PASSWORD / EMAIL rejected: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var inRole = await userManager.AddToRoleAsync(user, Roles.Admin);
        if (!inRole.Succeeded)
        {
            await userManager.DeleteAsync(user); // do not leave a role-less user behind; the next run retries cleanly
            throw new BootstrapConfigurationException("Assigning the Admin role failed: " + string.Join("; ", inRole.Errors.Select(e => e.Description)));
        }

        logger.LogInformation("Bootstrap: Admin user {Email} created", email);

        if (wantsCompany && !await db.Companies.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            db.Companies.Add(new Company { Name = companyName!, TaxNumber = companyTax!, IsActive = true });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Bootstrap: first company {Company} created", companyName);
        }

        return Outcome.AdminCreated;
    }

    private static string? Read(IConfiguration configuration, string envKey, string sectionKey) =>
        configuration[envKey] is { Length: > 0 } v ? v : configuration[sectionKey];

    private static bool IsValidEmail(string value) =>
        MailAddress.TryCreate(value, out var parsed) && string.Equals(parsed.Address, value, StringComparison.Ordinal);
}
