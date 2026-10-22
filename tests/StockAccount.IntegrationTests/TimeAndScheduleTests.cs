using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>Clock that can be shifted; the API resolves <see cref="TimeProvider"/> from DI (JWT, refresh tokens, audit).</summary>
public sealed class ShiftableTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}

/// <summary>
/// Time-based rules via the injectable TimeProvider and short configured intervals (CONTRACT §3.9, §4.1, §4.1 lockout):
/// access token expiry, refresh token expiry, lockout expiry, and the reconciliation background schedule (AutoFix).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TimeAndScheduleTests(ApiFixture fx)
{
    private (ApiFactory Factory, ShiftableTimeProvider Clock) ShiftedHost()
    {
        var clock = new ShiftableTimeProvider();
        var f = fx.CreateFactory(new Dictionary<string, string?>(), s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        return (f, clock);
    }

    [Fact]
    public async Task Access_token_expires_after_15_minutes()
    {
        var (f, clock) = ShiftedHost();
        clock.Offset = TimeSpan.FromMinutes(-16); // token issued "16 minutes ago" → exp 1 min in the past (> 30 s skew)
        var s = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword, f);
        clock.Offset = TimeSpan.Zero;
        await ApiSession.ExpectProblemAsync(await s.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized, "UNAUTHORIZED");

        // a token issued "14 minutes ago" is still valid
        clock.Offset = TimeSpan.FromMinutes(-14);
        var s2 = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword, f);
        clock.Offset = TimeSpan.Zero;
        await s2.GetJsonAsync("/api/auth/me");

        // the refresh cookie (7 days) still renews the expired session
        using var r = await s.RefreshRawAsync(s.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Refresh_token_expires_after_7_days()
    {
        var (f, clock) = ShiftedHost();
        clock.Offset = TimeSpan.FromDays(-7) - TimeSpan.FromMinutes(1);
        var old = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword, f);
        clock.Offset = TimeSpan.FromDays(-6);
        var recent = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword, f);
        clock.Offset = TimeSpan.Zero;

        await ApiSession.ExpectProblemAsync(await old.RefreshRawAsync(old.RefreshToken), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
        using var ok = await recent.RefreshRawAsync(recent.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Lockout_lasts_five_minutes_and_expires()
    {
        var admin = await fx.AdminAsync();
        var email = $"lock.{TestData.Code().ToLowerInvariant()}@test.local";
        var user = await admin.PostJsonAsync("/api/users", new { email, fullName = "Lock Test", password = "Passw0rdL", role = "User", companyId = fx.DemoCompanyId, isActive = true }, HttpStatusCode.Created);
        var s = fx.NewSession();
        for (var i = 0; i < 5; i++)
        {
            using var _ = await s.LoginRawAsync(email, "Wrong1234");
        }

        await ApiSession.ExpectProblemAsync(await s.LoginRawAsync(email, "Passw0rdL"), HttpStatusCode.Unauthorized, "ACCOUNT_LOCKED");
        var lockoutEnd = await fx.Factory.ScalarAsync<DateTimeOffset>("SELECT LockoutEnd FROM AspNetUsers WHERE Id = @id", new SqlParameter("@id", user["id"].Int()));
        var minutes = (lockoutEnd - DateTimeOffset.UtcNow).TotalMinutes;
        Assert.InRange(minutes, 4.5, 5.1);

        // Identity compares LockoutEnd with the system clock → simulate the 5 minutes passing by moving LockoutEnd into the past
        await fx.Factory.ExecuteSqlAsync("UPDATE AspNetUsers SET LockoutEnd = DATEADD(second, -1, SYSDATETIMEOFFSET()) WHERE Id = @id", new SqlParameter("@id", user["id"].Int()));
        await fx.LoginAsync(email, "Passw0rdL");
    }

    [Fact]
    public async Task Reconciliation_background_service_runs_on_schedule_and_autofixes()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 4);
        await fx.Factory.ExecuteSqlAsync($"UPDATE Stocks SET Balance = 99 WHERE Id = {stock["id"].Int()}");

        // second host with the background service enabled, first run after 1 s, AutoFix on
        var f = fx.CreateFactory(new Dictionary<string, string?>
        {
            ["Reconciliation:Enabled"] = "true",
            ["Reconciliation:InitialDelaySeconds"] = "1",
            ["Reconciliation:IntervalMinutes"] = "1",
            ["Reconciliation:AutoFix"] = "true",
        });
        _ = f.Server; // start host → BackgroundService starts

        decimal balance = 99;
        for (var i = 0; i < 40 && balance != 4; i++)
        {
            await Task.Delay(500);
            balance = await fx.Factory.ScalarAsync<decimal>($"SELECT Balance FROM Stocks WHERE Id = {stock["id"].Int()}");
        }

        Assert.Equal(4m, balance);
        var admin = await fx.AdminAsync();
        var after = await admin.PostJsonAsync("/api/admin/reconciliation/run", null, HttpStatusCode.OK);
        Assert.Empty(after["discrepancies"]!.AsArray());
    }
}
