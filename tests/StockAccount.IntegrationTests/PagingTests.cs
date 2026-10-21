using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §5.1, §11.2 #5: envelope, totalCount/totalPages, sort, q, filters, pageSize/sort validation.</summary>
[Collection(ApiCollection.Name)]
public sealed class PagingTests(ApiFixture fx)
{
    [Fact]
    public async Task Paging_envelope_sort_and_search()
    {
        var op = await fx.OperatorAsync();
        var prefix = TestData.Code("PG");
        for (var i = 1; i <= 5; i++)
        {
            await op.CreateStockAsync($"{prefix}-{i}", unit: i % 2 == 0 ? "Piece" : "Kg", unitPrice: i, name: $"Sayfa {i}");
        }

        var p1 = await op.GetJsonAsync($"/api/stocks?q={prefix}&pageSize=2&page=1&sort=code");
        Assert.Equal(5, p1["totalCount"].Int());
        Assert.Equal(3, p1["totalPages"].Int());
        Assert.Equal(1, p1["page"].Int());
        Assert.Equal(2, p1["pageSize"].Int());
        Assert.Equal(new[] { $"{prefix}-1", $"{prefix}-2" }, p1.Items().Select(s => s!["code"].Str()).ToArray());

        var p3 = await op.GetJsonAsync($"/api/stocks?q={prefix}&pageSize=2&page=3&sort=code");
        Assert.Equal(new[] { $"{prefix}-5" }, p3.Items().Select(s => s!["code"].Str()).ToArray());

        var desc = await op.GetJsonAsync($"/api/stocks?q={prefix}&sort=-code");
        Assert.Equal($"{prefix}-5", desc.Items()[0]!["code"].Str());

        var byPrice = await op.GetJsonAsync($"/api/stocks?q={prefix}&sort=-unitPrice,code");
        Assert.Equal(5m, byPrice.Items()[0]!["unitPrice"].Dec());

        var lower = await op.GetJsonAsync($"/api/stocks?q={prefix.ToLowerInvariant()}");
        Assert.Equal(5, lower["totalCount"].Int()); // case-insensitive

        var byName = await op.GetJsonAsync($"/api/stocks?q=Sayfa 3");
        Assert.Contains(byName.Items(), s => s!["code"].Str() == $"{prefix}-3");

        var pieces = await op.GetJsonAsync($"/api/stocks?q={prefix}&unit=Piece");
        Assert.Equal(2, pieces["totalCount"].Int());
        var beyond = await op.GetJsonAsync($"/api/stocks?q={prefix}&page=99");
        Assert.Empty(beyond.Items());

        var el = await op.GetJsonAsync("/api/stocks?q=EL");
        Assert.Contains(el.Items(), s => s!["code"].Str() == "ELMA");
    }

    [Fact]
    public async Task Default_page_size_is_20()
    {
        var op = await fx.OperatorAsync();
        var r = await op.GetJsonAsync("/api/stocks");
        Assert.Equal(20, r["pageSize"].Int());
        Assert.Equal(1, r["page"].Int());
    }

    [Theory]
    [InlineData("/api/stocks?pageSize=101", "pageSize")]
    [InlineData("/api/stocks?pageSize=0", "pageSize")]
    [InlineData("/api/stocks?page=0", "page")]
    [InlineData("/api/stocks?sort=hacked", "sort")]
    [InlineData("/api/stocks?sort=code;drop", "sort")]
    [InlineData("/api/current-accounts?sort=password", "sort")]
    [InlineData("/api/current-accounts?pageSize=500", "pageSize")]
    [InlineData("/api/invoices?sort=-foo", "sort")]
    [InlineData("/api/invoices?pageSize=101", "pageSize")]
    [InlineData("/api/stock-trans?sort=xyz", "sort")]
    [InlineData("/api/act-trans?sort=quantity", "sort")]
    [InlineData("/api/companies?sort=xyz", "sort")]
    [InlineData("/api/users?sort=passwordHash", "sort")]
    public async Task Invalid_paging_or_sort_is_400(string url, string errorKey)
    {
        var s = url.StartsWith("/api/companies") || url.StartsWith("/api/users") ? await fx.AdminAsync() : await fx.OperatorAsync();
        var p = await ApiSession.ExpectProblemAsync(await s.GetAsync(url), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(p["errors"]!.AsObject().ContainsKey(errorKey), p.ToJsonString());
    }

    [Fact]
    public async Task Portal_list_validation()
    {
        var portal = await fx.PortalAsync();
        await ApiSession.ExpectProblemAsync(await portal.GetAsync("/api/me/invoices?pageSize=101"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Invoice_filters_and_default_sort()
    {
        var op = await fx.OperatorAsync();
        var acc = await op.CreateAccountAsync("Both", title: "Filtre Cari");
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 10);
        var older = await op.PostJsonAsync("/api/invoices", new { type = "Sales", invoiceDate = "2026-01-15", currentAccountId = acc["id"].Int(), lines = new[] { new { stockId = stock["id"].Int(), quantity = 1m, unitPrice = 1m, vatRate = 1m } } }, HttpStatusCode.Created);
        var newer = await op.PostJsonAsync("/api/invoices", new { type = "Purchase", invoiceDate = "2026-02-15", currentAccountId = acc["id"].Int(), lines = new[] { new { stockId = stock["id"].Int(), quantity = 1m, unitPrice = 1m, vatRate = 1m } } }, HttpStatusCode.Created);

        var all = await op.GetJsonAsync($"/api/invoices?currentAccountId={acc["id"].Int()}");
        Assert.Equal(new[] { newer["id"].Int(), older["id"].Int() }, all.Items().Select(i => i!["id"].Int()).ToArray()); // -invoiceDate,-id

        Assert.Single((await op.GetJsonAsync($"/api/invoices?currentAccountId={acc["id"].Int()}&type=Purchase")).Items());
        Assert.Single((await op.GetJsonAsync($"/api/invoices?currentAccountId={acc["id"].Int()}&dateFrom=2026-02-01&dateTo=2026-02-28")).Items());
        Assert.Single((await op.GetJsonAsync($"/api/invoices?currentAccountId={acc["id"].Int()}&series=als")).Items());
        Assert.Equal(2, (await op.GetJsonAsync($"/api/invoices?q={acc["code"].Str()}"))["totalCount"].Int());
        Assert.Equal(2, (await op.GetJsonAsync($"/api/invoices?q=Filtre Cari&status=Draft&currentAccountId={acc["id"].Int()}"))["totalCount"].Int());

        var ok = await op.GetJsonAsync($"/api/invoices?currentAccountId={acc["id"].Int()}&sort=grandTotal,-documentNo,status,createdAt,invoiceDate");
        Assert.Equal(2, ok["totalCount"].Int());
    }
}
