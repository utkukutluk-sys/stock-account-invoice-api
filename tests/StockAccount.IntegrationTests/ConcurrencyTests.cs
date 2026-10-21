using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §3.6, §11.2 #6: stale rowVersion 409; parallel approvals never oversell and never duplicate numbers.</summary>
[Collection(ApiCollection.Name)]
public sealed class ConcurrencyTests(ApiFixture fx)
{
    [Fact]
    public async Task Put_with_stale_rowversion_is_409()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        var id = stock["id"].Int();
        var code = stock["code"].Str();
        await op.PutJsonAsync($"/api/stocks/{id}", TestData.StockBody(code, name: "first", rowVersion: stock["rowVersion"].Str()));
        await ApiSession.ExpectProblemAsync(await op.PutAsync($"/api/stocks/{id}", TestData.StockBody(code, name: "second", rowVersion: stock["rowVersion"].Str())), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        Assert.Equal("first", (await op.GetJsonAsync($"/api/stocks/{id}"))["name"].Str());

        var acc = await op.CreateAccountAsync();
        await op.PutJsonAsync($"/api/current-accounts/{acc["id"].Int()}", TestData.AccountBody(acc["code"].Str(), title: "x", rowVersion: acc["rowVersion"].Str()));
        await ApiSession.ExpectProblemAsync(await op.PutAsync($"/api/current-accounts/{acc["id"].Int()}", TestData.AccountBody(acc["code"].Str(), title: "y", rowVersion: acc["rowVersion"].Str())), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Parallel_updates_of_same_stock_one_wins()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        var rv = stock["rowVersion"].Str();
        var tasks = Enumerable.Range(0, 5).Select(i => op.PutAsync($"/api/stocks/{stock["id"].Int()}", TestData.StockBody(stock["code"].Str(), name: $"p{i}", rowVersion: rv))).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Theory]
    [InlineData(2, 6, 10)]  // two invoices of 6 against balance 10 → exactly one succeeds
    [InlineData(6, 3, 10)]  // six invoices of 3 against balance 10 → exactly three succeed (retries must kick in)
    public async Task Parallel_approvals_draining_same_stock_never_oversell(int invoices, decimal qtyEach, decimal balance)
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), balance);
        var customer = await op.CreateAccountAsync("Customer");

        var drafts = new List<System.Text.Json.Nodes.JsonNode>();
        for (var i = 0; i < invoices; i++)
        {
            drafts.Add(await op.CreateInvoiceAsync("Sales", customer["id"].Int(), (stock["id"].Int(), qtyEach, 1m, 1m)));
        }

        // separate sessions → separate requests in parallel
        var sessions = new List<ApiSession>();
        for (var i = 0; i < invoices; i++)
        {
            sessions.Add(await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword));
        }

        var responses = await Task.WhenAll(drafts.Select((d, i) => sessions[i].ApproveRawAsync(d)));
        var codes = new List<string>();
        foreach (var r in responses)
        {
            var text = await r.Content.ReadAsStringAsync();
            codes.Add(r.StatusCode == HttpStatusCode.OK ? "OK" : System.Text.Json.Nodes.JsonNode.Parse(text)?["code"]?.GetValue<string>() ?? ((int)r.StatusCode).ToString());
        }

        var maxOk = (int)Math.Floor(balance / qtyEach);
        var okCount = codes.Count(c => c == "OK");
        // Never oversell. Losers get INSUFFICIENT_STOCK, or 409 CONCURRENCY_CONFLICT once the 3 retries are exhausted (CONTRACT §3.6).
        Assert.True(okCount >= 1 && okCount <= maxOk, "results: " + string.Join(",", codes));
        Assert.All(codes.Where(c => c != "OK"), c => Assert.True(c is "INSUFFICIENT_STOCK" or "CONCURRENCY_CONFLICT", c));
        if (invoices == 2)
        {
            Assert.Equal(new[] { "INSUFFICIENT_STOCK", "OK" }, codes.Order().ToArray()); // one retry is always enough for two contenders
        }

        var expectedOk = okCount;

        var final = await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}");
        Assert.Equal(balance - (expectedOk * qtyEach), final["balance"].Dec());
        Assert.True(final["balance"].Dec() >= 0);

        // stored balance equals ledger; no duplicate numbers
        var ledger = await fx.Factory.ScalarAsync<decimal>($"SELECT SUM(CASE WHEN Direction = 1 THEN Quantity ELSE -Quantity END) FROM StockTrans WHERE StockId = {stock["id"].Int()}");
        Assert.Equal(final["balance"].Dec(), ledger);
        var dup = await fx.Factory.ScalarAsync<int>($"SELECT COUNT(*) FROM (SELECT Series, Number FROM Invoices WHERE CompanyId = {fx.DemoCompanyId} AND Number IS NOT NULL GROUP BY Series, Number HAVING COUNT(*) > 1) x");
        Assert.Equal(0, dup);
        var accBalance = (await op.GetJsonAsync($"/api/current-accounts/{customer["id"].Int()}"))["balance"].Dec();
        var accLedger = await fx.Factory.ScalarAsync<decimal>($"SELECT ISNULL(SUM(CASE WHEN Direction = 1 THEN Amount ELSE -Amount END),0) FROM ActTrans WHERE CurrentAccountId = {customer["id"].Int()}");
        Assert.Equal(accLedger, accBalance);
    }

    [Fact]
    public async Task Parallel_approvals_of_different_stocks_get_unique_sequential_numbers()
    {
        var op = await fx.OperatorAsync();
        var customer = await op.CreateAccountAsync("Customer");
        var series = "PAR" + Random.Shared.Next(1000, 9999);
        var drafts = new List<System.Text.Json.Nodes.JsonNode>();
        for (var i = 0; i < 4; i++)
        {
            var stock = await op.CreateStockAsync();
            await op.PurchaseAsync(stock["id"].Int(), 5);
            drafts.Add(await op.PostJsonAsync("/api/invoices", TestData.InvoiceBody("Sales", customer["id"].Int(), [(stock["id"].Int(), 1, 1, 1)], series: series), HttpStatusCode.Created));
        }

        var sessions = new List<ApiSession>();
        for (var i = 0; i < drafts.Count; i++)
        {
            sessions.Add(await fx.LoginAsync(ApiFixture.DemoEmail, ApiFixture.DemoPassword));
        }

        var responses = await Task.WhenAll(drafts.Select((d, i) => sessions[i].ApproveRawAsync(d)));
        var statuses = responses.Select(r => (int)r.StatusCode).ToList();
        var numbers = await fx.Factory.QueryAsync($"SELECT Number FROM Invoices WHERE Series = '{series}' AND Number IS NOT NULL ORDER BY Number");
        var okCount = statuses.Count(s => s == 200);
        // Same account row is updated by all four → retries; up to 3 attempts each. At least 2 must succeed; numbers gap-free.
        Assert.True(okCount >= 2, string.Join(",", statuses));
        Assert.Equal(Enumerable.Range(1, okCount).ToArray(), numbers.Select(n => (int)n["Number"]!).ToArray());
        Assert.All(statuses, s => Assert.True(s is 200 or 409, s.ToString()));
    }
}
