using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §5.3, §6, §11.2 #7–8: X-Cache HIT/MISS, invalidation on CUD and on invoice approve/cancel, ETag/304.</summary>
[Collection(ApiCollection.Name)]
public sealed class CacheAndETagTests(ApiFixture fx)
{
    private static string XCache(HttpResponseMessage r) => r.Headers.TryGetValues("X-Cache", out var v) ? v.Single() : "(none)";

    private static async Task<(string Cache, System.Text.Json.Nodes.JsonNode Body)> GetWithCache(ApiSession s, string url)
    {
        using var r = await s.GetAsync(url);
        var cache = XCache(r);
        var body = await ApiSession.ExpectJsonAsync(r, HttpStatusCode.OK);
        return (cache, body);
    }

    [Fact]
    public async Task Stock_by_id_miss_then_hit_and_invalidated_by_put()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        var url = $"/api/stocks/{stock["id"].Int()}";

        var (c1, _) = await GetWithCache(op, url);
        var (c2, b2) = await GetWithCache(op, url);
        Assert.Equal("MISS", c1);
        Assert.Equal("HIT", c2);

        await op.PutJsonAsync(url, TestData.StockBody(stock["code"].Str(), name: "Cache Yeni", rowVersion: b2["rowVersion"].Str()));
        var (c3, b3) = await GetWithCache(op, url);
        Assert.Equal("MISS", c3);
        Assert.Equal("Cache Yeni", b3["name"].Str());
        Assert.Equal("HIT", (await GetWithCache(op, url)).Cache);
    }

    [Fact]
    public async Task Stock_list_cached_and_invalidated_by_create_and_delete()
    {
        var op = await fx.OperatorAsync();
        var prefix = TestData.Code("LC");
        await op.CreateStockAsync($"{prefix}-1");
        var url = $"/api/stocks?q={prefix}&sort=code";

        Assert.Equal("MISS", (await GetWithCache(op, url)).Cache);
        var (hit, body) = await GetWithCache(op, url);
        Assert.Equal("HIT", hit);
        Assert.Equal(1, body["totalCount"].Int());

        // same query, different parameter order → same cache entry (normalized hash)
        Assert.Equal("HIT", (await GetWithCache(op, $"/api/stocks?sort=code&q={prefix}&page=1&pageSize=20")).Cache);

        var second = await op.CreateStockAsync($"{prefix}-2");
        var (afterCreate, b) = await GetWithCache(op, url);
        Assert.Equal("MISS", afterCreate);
        Assert.Equal(2, b["totalCount"].Int());

        var admin = await fx.AdminDemoAsync();
        using (var del = await admin.DeleteAsync($"/api/stocks/{second["id"].Int()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        }

        var (afterDelete, b2) = await GetWithCache(op, url);
        Assert.Equal("MISS", afterDelete);
        Assert.Equal(1, b2["totalCount"].Int());
    }

    [Fact]
    public async Task Approve_and_cancel_invalidate_stock_and_account_entries()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 10);
        var acc = await op.CreateAccountAsync("Customer");
        var stockUrl = $"/api/stocks/{stock["id"].Int()}";
        var accUrl = $"/api/current-accounts/{acc["id"].Int()}";
        var listUrl = $"/api/stocks?q={stock["code"].Str()}";

        // warm caches
        await GetWithCache(op, stockUrl);
        Assert.Equal("HIT", (await GetWithCache(op, stockUrl)).Cache);
        await GetWithCache(op, accUrl);
        Assert.Equal("HIT", (await GetWithCache(op, accUrl)).Cache);
        await GetWithCache(op, listUrl);
        Assert.Equal("HIT", (await GetWithCache(op, listUrl)).Cache);

        // invoice draft CRUD does not invalidate
        var draft = await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 4m, 10m, 1m));
        Assert.Equal("HIT", (await GetWithCache(op, stockUrl)).Cache);

        var approved = await op.ApproveAsync(draft);
        var (sc, sb) = await GetWithCache(op, stockUrl);
        Assert.Equal("MISS", sc);
        Assert.Equal(6m, sb["balance"].Dec());
        var (ac, ab) = await GetWithCache(op, accUrl);
        Assert.Equal("MISS", ac);
        Assert.Equal(40.40m, ab["balance"].Dec());
        var (lc, lb) = await GetWithCache(op, listUrl);
        Assert.Equal("MISS", lc);
        Assert.Equal(6m, lb.Items()[0]!["balance"].Dec());

        // admin "all companies" scope list also refreshed
        var adminAll = await fx.AdminAsync();
        var allUrl = $"/api/stocks?q={stock["code"].Str()}";
        Assert.Equal(6m, (await adminAll.GetJsonAsync(allUrl)).Items()[0]!["balance"].Dec());
        Assert.Equal("HIT", (await GetWithCache(adminAll, allUrl)).Cache);

        var admin = await fx.AdminDemoAsync();
        await ApiSession.ExpectJsonAsync(await admin.CancelRawAsync(approved), HttpStatusCode.OK);
        Assert.Equal(10m, (await op.GetJsonAsync(stockUrl))["balance"].Dec());
        Assert.Equal(0m, (await op.GetJsonAsync(accUrl))["balance"].Dec());
        var (allAfter, allBody) = await GetWithCache(adminAll, allUrl);
        Assert.Equal("MISS", allAfter);
        Assert.Equal(10m, allBody.Items()[0]!["balance"].Dec());
    }

    [Fact]
    public async Task Cached_by_id_entry_respects_tenant()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        var url = $"/api/stocks/{stock["id"].Int()}";
        await op.GetJsonAsync(url); // cache it

        var admin = await fx.AdminAsync();
        var company = await admin.PostJsonAsync("/api/companies", new { name = "Cache Tenant", taxNumber = TestData.TaxNumber(), isActive = true }, HttpStatusCode.Created);
        var email = $"ct.{TestData.Code().ToLowerInvariant()}@test.local";
        await admin.PostJsonAsync("/api/users", new { email, fullName = "C T", password = "Passw0rdC", role = "User", companyId = company["id"].Int(), isActive = true }, HttpStatusCode.Created);
        var other = await fx.LoginAsync(email, "Passw0rdC");
        await ApiSession.ExpectProblemAsync(await other.GetAsync(url), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Account_list_has_x_cache_header()
    {
        var op = await fx.OperatorAsync();
        var acc = await op.CreateAccountAsync();
        var url = $"/api/current-accounts?q={acc["code"].Str()}";
        Assert.Equal("MISS", (await GetWithCache(op, url)).Cache);
        Assert.Equal("HIT", (await GetWithCache(op, url)).Cache);
        await op.PutJsonAsync($"/api/current-accounts/{acc["id"].Int()}", TestData.AccountBody(acc["code"].Str(), title: "Değişti", rowVersion: acc["rowVersion"].Str()));
        var (c, b) = await GetWithCache(op, url);
        Assert.Equal("MISS", c);
        Assert.Equal("Değişti", b.Items()[0]!["title"].Str());
    }

    [Theory]
    [InlineData("/api/stocks")]
    [InlineData("/api/current-accounts")]
    [InlineData("/api/invoices")]
    [InlineData("/api/stock-trans")]
    [InlineData("/api/dashboard/summary")]
    public async Task ETag_and_304(string url)
    {
        var op = await fx.OperatorAsync();
        using var r1 = await op.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var etag = r1.Headers.ETag;
        Assert.NotNull(etag);
        Assert.True(etag.IsWeak);
        Assert.Matches("^\"[0-9a-f]{16}\"$", etag.Tag);
        Assert.True(r1.Headers.CacheControl!.Private && r1.Headers.CacheControl.NoCache, r1.Headers.CacheControl.ToString());

        using var r2 = await op.GetAsync(url, r => r.Headers.TryAddWithoutValidation("If-None-Match", etag.ToString()));
        Assert.Equal(HttpStatusCode.NotModified, r2.StatusCode);
        Assert.Empty(await r2.Content.ReadAsByteArrayAsync());

        using var r3 = await op.GetAsync(url, r => r.Headers.TryAddWithoutValidation("If-None-Match", "W/\"0000000000000000\""));
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
    }

    [Fact]
    public async Task ETag_changes_after_mutation_and_not_on_auth_routes()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        var url = $"/api/stocks/{stock["id"].Int()}";
        using var r1 = await op.GetAsync(url);
        var e1 = r1.Headers.ETag!.ToString();
        await op.PutJsonAsync(url, TestData.StockBody(stock["code"].Str(), name: "etag2", rowVersion: stock["rowVersion"].Str()));
        using var r2 = await op.GetAsync(url, r => r.Headers.TryAddWithoutValidation("If-None-Match", e1));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.NotEqual(e1, r2.Headers.ETag!.ToString());

        using var me = await op.GetAsync("/api/auth/me");
        Assert.Null(me.Headers.ETag);
    }
}

[Collection(ApiCollection.Name)]
public sealed class CacheCompanyAndSearchTests(ApiFixture fx)
{
    [Fact]
    public async Task Company_update_refreshes_company_name_in_cached_stock_lists()
    {
        var admin = await fx.AdminAsync();
        var tax = TestData.TaxNumber();
        var company = await admin.PostJsonAsync("/api/companies", new { name = "Eski Ad", taxNumber = tax, isActive = true }, HttpStatusCode.Created);
        var scoped = await fx.LoginAsync(ApiFixture.AdminEmail, ApiFixture.AdminPassword);
        scoped.CompanyId = company["id"].Int();
        var stock = await scoped.CreateStockAsync();
        var url = $"/api/stocks?q={stock["code"].Str()}";
        Assert.Equal("Eski Ad", (await scoped.GetJsonAsync(url)).Items()[0]!["companyName"].Str());
        Assert.Equal("Eski Ad", (await scoped.GetJsonAsync($"/api/stocks/{stock["id"].Int()}"))["companyName"].Str());

        await admin.PutJsonAsync($"/api/companies/{company["id"].Int()}", new { name = "Yeni Ad", taxNumber = tax, isActive = true, rowVersion = company["rowVersion"].Str() });
        Assert.Equal("Yeni Ad", (await scoped.GetJsonAsync(url)).Items()[0]!["companyName"].Str());
    }

    [Fact]
    public async Task Lowercase_ascii_search_finds_codes_with_I_under_turkish_collation()
    {
        var op = await fx.OperatorAsync();
        var code = "KIRAZ" + TestData.Code("X");
        await op.CreateStockAsync(code, name: "Kiraz");
        var r = await op.GetJsonAsync($"/api/stocks?q={code.ToLowerInvariant()}");
        Assert.Contains(r.Items(), s => s!["code"].Str() == code);
    }
}
