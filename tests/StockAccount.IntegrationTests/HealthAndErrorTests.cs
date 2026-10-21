using System.Net;
using System.Text.Json.Nodes;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §11.2 #14 + infrastructure sanity: health, ProblemDetails shape, correlation id, seed.</summary>
[Collection(ApiCollection.Name)]
public sealed class HealthAndErrorTests(ApiFixture fx)
{
    [Fact]
    public void Host_uses_throwaway_test_database()
    {
        Assert.StartsWith("StockAccountTests_", fx.Factory.ActualDatabaseName());
    }

    [Fact]
    public async Task Health_returns_200_with_sql_check()
    {
        using var res = await fx.NewSession().Client.GetAsync("/health");
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonNode.Parse(text)!;
        // In-memory cache fallback (Cache:UseRedis=false) → Redis reported Degraded, SQL Healthy.
        Assert.Contains(json["status"].Str(), new[] { "Healthy", "Degraded" });
        Assert.Contains("sql", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_and_generated()
    {
        var s = fx.NewSession();
        using var res = await s.SendAsync(HttpMethod.Get, "/api/stocks", auth: false, configure: r => r.Headers.Add("X-Correlation-Id", "it-corr-123"));
        Assert.Equal("it-corr-123", res.Headers.GetValues("X-Correlation-Id").Single());
        var problem = await ApiSession.ExpectProblemAsync(res, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Equal("it-corr-123", problem["correlationId"].Str());

        using var res2 = await s.Client.GetAsync("/health");
        Assert.False(string.IsNullOrWhiteSpace(res2.Headers.GetValues("X-Correlation-Id").Single()));
    }

    [Fact]
    public async Task Unknown_route_returns_problem_404()
    {
        var admin = await fx.AdminAsync();
        await ApiSession.ExpectProblemAsync(await admin.GetAsync("/api/does-not-exist"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Validation_problem_has_camelCase_errors()
    {
        var op = await fx.OperatorAsync();
        var p = await ApiSession.ExpectProblemAsync(
            await op.PostAsync("/api/stocks", new { code = "", name = "", unit = "Kg", vatRate = 7, unitPrice = -1, isActive = true }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var errors = p["errors"]!.AsObject();
        Assert.True(errors.ContainsKey("code"), errors.ToJsonString());
        Assert.True(errors.ContainsKey("vatRate"), errors.ToJsonString());
        Assert.True(errors.ContainsKey("unitPrice"), errors.ToJsonString());
        Assert.False(string.IsNullOrWhiteSpace(p["detail"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Seed_data_present_and_opening_invoice_approved()
    {
        var op = await fx.OperatorAsync();
        var invoices = await op.GetJsonAsync("/api/invoices?series=ALS&status=Approved");
        Assert.Contains(invoices.Items(), i => i!["documentNo"].Str() == "ALS000001");

        var supplier = (await op.GetJsonAsync("/api/current-accounts?q=S001")).Items().Single(a => a!["code"].Str() == "S001")!;
        Assert.True(supplier["balance"].Dec() <= -18714.00m, supplier.ToJsonString());

        var stocks = (await op.GetJsonAsync("/api/stocks?pageSize=100")).Items().Select(x => x!["code"].Str()).ToList();
        foreach (var code in new[] { "ELMA", "ARMUT", "SUT", "EKMEK", "DETERJAN" })
        {
            Assert.Contains(code, stocks);
        }
    }

    [Fact]
    public async Task Enums_are_strings_and_dates_iso()
    {
        var op = await fx.OperatorAsync();
        var stock = (await op.GetJsonAsync("/api/stocks?q=ELMA")).Items().First()!;
        Assert.Equal("Kg", stock["unit"].Str());
        Assert.EndsWith("Z", stock["createdAt"].Str());
        var inv = (await op.GetJsonAsync("/api/invoices?series=ALS")).Items().First()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", inv["invoiceDate"].Str());
        Assert.Equal("Purchase", inv["type"].Str());
    }
}
