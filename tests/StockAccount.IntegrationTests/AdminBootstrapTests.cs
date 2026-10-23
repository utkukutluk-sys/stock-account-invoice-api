using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StockAccount.Infrastructure.Startup;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>
/// One-time admin bootstrap of the --migrate-only step (production without demo seed): creates the Admin once, is
/// idempotent, enforces the password policy, never logs the password and fails clearly in Production when unconfigured.
/// Each test uses its own migrated, NOT seeded database.
/// </summary>
public sealed class AdminBootstrapTests : IAsyncLifetime
{
    private const string Email = "owner@example.com";
    private const string Password = "Str0ng-Bootstrap-Pass";

    private string connectionString = null!;
    private ApiFactory factory = null!;

    public Task InitializeAsync()
    {
        connectionString = ApiFactory.NewConnectionString();
        factory = new ApiFactory(connectionString, new Dictionary<string, string?> { ["Database:Seed"] = "false" });
        _ = factory.Server; // migrates, no seed
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await ApiFactory.DropDatabaseAsync(connectionString);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public async Task Creates_admin_and_company_once_and_can_log_in_and_is_idempotent()
    {
        var log = new ListLogger();
        var config = Config(
            ("BOOTSTRAP_ADMIN_EMAIL", Email), ("BOOTSTRAP_ADMIN_PASSWORD", Password),
            ("BOOTSTRAP_COMPANY_NAME", "Ilk Sirket A.S."), ("BOOTSTRAP_COMPANY_TAX_NUMBER", "1234567890"));

        Assert.Equal(AdminBootstrapper.Outcome.AdminCreated, await AdminBootstrapper.EnsureAsync(factory.Services, config, requireAdmin: true, log));
        Assert.Equal(1, await factory.ScalarAsync<int>("SELECT COUNT(*) FROM Companies WHERE Name = N'Ilk Sirket A.S.'"));
        Assert.Equal(0, await factory.ScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers WHERE Email LIKE '%stockaccount.local'"));

        var session = new ApiSession(factory.CreateClient());
        await session.LoginAsync(Email, Password);
        Assert.Equal("Admin", session.User!["role"]?.ToString() ?? session.User["roles"]?[0]?.ToString());

        // second run (e.g. every deploy): nothing changes, even with a different password configured
        var other = Config(("BOOTSTRAP_ADMIN_EMAIL", "second@example.com"), ("BOOTSTRAP_ADMIN_PASSWORD", "Another-Pass-123"));
        Assert.Equal(AdminBootstrapper.Outcome.AdminAlreadyExists, await AdminBootstrapper.EnsureAsync(factory.Services, other, requireAdmin: true, log));
        Assert.Equal(1, await factory.ScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers"));
        Assert.Equal(1, await factory.ScalarAsync<int>("SELECT COUNT(*) FROM Companies"));

        // the password is never written to the log
        Assert.DoesNotContain(log.Lines, l => l.Contains(Password, StringComparison.Ordinal) || l.Contains("Another-Pass-123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Weak_password_is_rejected_and_no_user_is_created()
    {
        var config = Config(("BOOTSTRAP_ADMIN_EMAIL", Email), ("BOOTSTRAP_ADMIN_PASSWORD", "weak"));
        var ex = await Assert.ThrowsAsync<BootstrapConfigurationException>(() => AdminBootstrapper.EnsureAsync(factory.Services, config, requireAdmin: true, new ListLogger()));
        Assert.DoesNotContain("weak", ex.Message.Replace("BOOTSTRAP_ADMIN_PASSWORD", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(0, await factory.ScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers"));
    }

    [Fact]
    public async Task Production_without_bootstrap_settings_fails_with_a_clear_message()
    {
        var ex = await Assert.ThrowsAsync<BootstrapConfigurationException>(
            () => AdminBootstrapper.EnsureAsync(factory.Services, Config(), requireAdmin: true, new ListLogger()));
        Assert.Contains("BOOTSTRAP_ADMIN_EMAIL", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BOOTSTRAP_ADMIN_PASSWORD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_production_without_settings_is_skipped()
    {
        Assert.Equal(AdminBootstrapper.Outcome.Skipped, await AdminBootstrapper.EnsureAsync(factory.Services, Config(), requireAdmin: false, new ListLogger()));
        Assert.Equal(0, await factory.ScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers"));
    }

    [Fact]
    public async Task Company_name_without_tax_number_is_rejected()
    {
        var config = Config(("BOOTSTRAP_ADMIN_EMAIL", Email), ("BOOTSTRAP_ADMIN_PASSWORD", Password), ("BOOTSTRAP_COMPANY_NAME", "X"));
        await Assert.ThrowsAsync<BootstrapConfigurationException>(() => AdminBootstrapper.EnsureAsync(factory.Services, config, requireAdmin: true, new ListLogger()));
        Assert.Equal(0, await factory.ScalarAsync<int>("SELECT COUNT(*) FROM AspNetUsers"));
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception?.ToString() ?? string.Empty));
    }
}
