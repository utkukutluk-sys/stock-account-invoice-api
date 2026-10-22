using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using StockAccount.Application.Features.Dashboard;
using StockAccount.Application.Features.Dashboard.Dtos;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>Production readiness (BACKEND_NOTES §7): rate limiting, fail-fast config, error hardening, health split, schema guard.</summary>
[Collection(ApiCollection.Name)]
public sealed class ProductionReadinessTests(ApiFixture fx)
{
    private const string StrongKey = "prod-test-key-9f3c1a7e5b2d4c6e8a0b1c2d3e4f5a6b7c8d9e0f";

    private sealed class ThrowingDashboard : IDashboardService
    {
        public Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret internal detail from StockAccount.Infrastructure");
    }

    private static void AssertRateLimited(HttpResponseMessage res, JsonNode problem)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("RATE_LIMITED", problem["code"].Str());
        Assert.Contains("saniye", problem["detail"].Str());
        Assert.True(res.Headers.RetryAfter?.Delta is { TotalSeconds: >= 1 }, "Retry-After header missing");
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client_with_retry_after()
    {
        var f = fx.CreateFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:Global:PermitLimit"] = "1000",
            ["RateLimiting:Global:WindowSeconds"] = "60",
            ["RateLimiting:Auth:PermitLimit"] = "3",
            ["RateLimiting:Auth:WindowSeconds"] = "60",
        });
        var anon = fx.NewSession(f);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await anon.LoginRawAsync($"nobody{i}@x.local", "Wrong1234");
            Assert.Equal(HttpStatusCode.Unauthorized, ok.StatusCode);
        }

        using var limited = await anon.LoginRawAsync("nobody@x.local", "Wrong1234");
        AssertRateLimited(limited, JsonNode.Parse(await limited.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task Global_limit_applies_to_api_but_not_to_health()
    {
        var f = fx.CreateFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:Global:PermitLimit"] = "5",
            ["RateLimiting:Global:WindowSeconds"] = "60",
            ["RateLimiting:Auth:PermitLimit"] = "100",
            ["RateLimiting:Auth:WindowSeconds"] = "60",
        });
        var anon = fx.NewSession(f);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            last?.Dispose();
            last = await anon.SendAsync(HttpMethod.Get, "/api/stocks", auth: false);
        }

        using (last)
        {
            AssertRateLimited(last!, JsonNode.Parse(await last!.Content.ReadAsStringAsync())!);
        }

        for (var i = 0; i < 20; i++)
        {
            using var h = await anon.Client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, h.StatusCode);
        }
    }

    [Theory]
    [InlineData("Jwt:Key", null)] // placeholder from appsettings.json ("DEV-ONLY-change-me-…")
    [InlineData("Jwt:Key", "")]
    [InlineData("Jwt:Key", "too-short-but-real")]
    [InlineData("ConnectionStrings:Default", "")]
    public void Production_refuses_to_start_with_missing_or_placeholder_settings(string key, string? value)
    {
        var overrides = new Dictionary<string, string?> { ["environment"] = "Production", ["Jwt:Key"] = StrongKey };
        if (value is null)
        {
            overrides.Remove(key);
        }
        else
        {
            overrides[key] = value;
        }

        var f = fx.CreateFactory(overrides);
        Assert.ThrowsAny<Exception>(() => f.Server);
    }

    [Fact]
    public async Task Production_hides_internals_on_500_404_405_and_sets_security_headers()
    {
        var f = fx.CreateFactory(
            new Dictionary<string, string?> { ["environment"] = "Production", ["Jwt:Key"] = StrongKey },
            services => services.AddScoped<IDashboardService, ThrowingDashboard>());
        var op = await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword, f);

        using (var res = await op.GetAsync("/api/dashboard/summary"))
        {
            var text = await res.Content.ReadAsStringAsync();
            var p = await ApiSession.ExpectProblemAsync(res, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
            Assert.Null(p["exception"]);
            Assert.DoesNotContain("secret internal detail", text);
            Assert.DoesNotContain("InvalidOperationException", text);
            Assert.DoesNotContain(" at ", text);
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
            Assert.False(res.Headers.Contains("Server"));
        }

        await ApiSession.ExpectProblemAsync(await op.GetAsync("/api/does-not-exist"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await op.DeleteAsync("/api/dashboard/summary"), HttpStatusCode.MethodNotAllowed, "NOT_FOUND");

        using var swagger = await op.Client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.NotFound, swagger.StatusCode); // Swagger is off in Production

        using var ready = await op.Client.GetAsync("/health/ready");
        var health = JsonNode.Parse(await ready.Content.ReadAsStringAsync())!;
        Assert.Null(health["checks"]!["redis"]!["description"]); // no internal details outside Development
    }

    [Fact]
    public async Task Health_live_has_no_dependencies_and_ready_checks_sql_and_redis()
    {
        var anon = fx.NewSession();

        using var live = await anon.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var liveJson = JsonNode.Parse(await live.Content.ReadAsStringAsync())!;
        Assert.Equal("Healthy", liveJson["status"].Str());
        Assert.Empty(liveJson["checks"]!.AsObject());

        foreach (var url in new[] { "/health/ready", "/health" })
        {
            using var ready = await anon.Client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            var json = JsonNode.Parse(await ready.Content.ReadAsStringAsync())!;
            Assert.Equal("Healthy", json["checks"]!["sql"]!["status"].Str());
            Assert.Equal("Degraded", json["checks"]!["redis"]!["status"].Str()); // tests use the in-memory cache
            Assert.NotNull(json["checks"]!["sql"]!["durationMs"]);
        }
    }

    [Fact]
    public async Task Api_refuses_to_start_on_a_database_with_newer_migrations()
    {
        var cs = ApiFactory.NewConnectionString();
        try
        {
            await using (var first = new ApiFactory(cs))
            {
                _ = first.Server; // migrate + seed
                await first.ExecuteSqlAsync(
                    "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES (@id, '99.0.0')",
                    new SqlParameter("@id", "29990101000000_FromTheFuture"));
            }

            await using var second = new ApiFactory(cs);
            Assert.ThrowsAny<Exception>(() => second.Server);
        }
        finally
        {
            await ApiFactory.DropDatabaseAsync(cs);
        }
    }
}
