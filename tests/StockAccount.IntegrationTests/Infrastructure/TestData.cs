using System.Net;
using System.Text.Json.Nodes;

namespace StockAccount.IntegrationTests.Infrastructure;

/// <summary>Builders for uniquely coded test data (so tests are independent and re-runnable).</summary>
public static class TestData
{
    private static int counter;

    public static string Code(string prefix = "T") =>
        $"{prefix}{DateTime.UtcNow:HHmmss}{Interlocked.Increment(ref counter):D4}";

    public static string TaxNumber() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static object StockBody(string? code = null, string name = "Test Stok", string unit = "Kg", decimal vatRate = 1, decimal unitPrice = 10, bool isActive = true, string? rowVersion = null) =>
        new { code = code ?? Code("S"), name, unit, vatRate, unitPrice, isActive, rowVersion };

    public static object AccountBody(string? code = null, string title = "Test Cari", string type = "Both", bool isActive = true, string? city = null, string? rowVersion = null) =>
        new { code = code ?? Code("C"), title, type, isActive, city, rowVersion };

    public static async Task<JsonNode> CreateStockAsync(this ApiSession s, string? code = null, string unit = "Kg", decimal vatRate = 1, decimal unitPrice = 10, bool isActive = true, string name = "Test Stok") =>
        await s.PostJsonAsync("/api/stocks", StockBody(code, name, unit, vatRate, unitPrice, isActive), HttpStatusCode.Created);

    public static async Task<JsonNode> CreateAccountAsync(this ApiSession s, string type = "Both", string? code = null, string title = "Test Cari", string? city = null) =>
        await s.PostJsonAsync("/api/current-accounts", AccountBody(code, title, type, city: city), HttpStatusCode.Created);

    public static object InvoiceBody(string type, int accountId, IEnumerable<(int StockId, decimal Qty, decimal Price, decimal Vat)> lines, string? series = null, string? rowVersion = null, string? description = null) =>
        new
        {
            type,
            series,
            invoiceDate = Today,
            currentAccountId = accountId,
            description,
            rowVersion,
            lines = lines.Select(l => new { stockId = l.StockId, quantity = l.Qty, unitPrice = l.Price, vatRate = l.Vat }).ToArray(),
        };

    public static async Task<JsonNode> CreateInvoiceAsync(this ApiSession s, string type, int accountId, params (int StockId, decimal Qty, decimal Price, decimal Vat)[] lines) =>
        await s.PostJsonAsync("/api/invoices", InvoiceBody(type, accountId, lines), HttpStatusCode.Created);

    public static Task<HttpResponseMessage> ApproveRawAsync(this ApiSession s, JsonNode invoice) =>
        s.PostAsync($"/api/invoices/{invoice["id"].Int()}/approve", new { rowVersion = invoice["rowVersion"].Str() });

    public static async Task<JsonNode> ApproveAsync(this ApiSession s, JsonNode invoice) =>
        await ApiSession.ExpectJsonAsync(await s.ApproveRawAsync(invoice), HttpStatusCode.OK);

    public static Task<HttpResponseMessage> CancelRawAsync(this ApiSession s, JsonNode invoice, string reason = "Test iptali") =>
        s.PostAsync($"/api/invoices/{invoice["id"].Int()}/cancel", new { rowVersion = invoice["rowVersion"].Str(), reason });

    /// <summary>Gives a fresh stock some balance by approving a purchase from a fresh supplier.</summary>
    public static async Task<JsonNode> PurchaseAsync(this ApiSession s, int stockId, decimal qty, decimal price = 10, decimal vat = 1)
    {
        var supplier = await s.CreateAccountAsync("Supplier");
        var inv = await s.CreateInvoiceAsync("Purchase", supplier["id"].Int(), (stockId, qty, price, vat));
        return await s.ApproveAsync(inv);
    }
}
