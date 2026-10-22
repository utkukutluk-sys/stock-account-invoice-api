using System.Net;
using System.Text.Json.Nodes;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>Swagger regression (BUG-3): every contract endpoint documented with a 2xx response and the right auth requirement.</summary>
[Collection(ApiCollection.Name)]
public sealed class SwaggerTests(ApiFixture fx)
{
    private static readonly string[] Anonymous = ["POST /api/auth/login", "POST /api/auth/refresh", "POST /api/auth/logout"];

    public static TheoryData<string> ContractOperations => new()
    {
        "POST /api/auth/login", "POST /api/auth/refresh", "POST /api/auth/logout", "GET /api/auth/me", "POST /api/auth/change-password",
        "GET /api/companies", "GET /api/companies/{id}", "GET /api/companies/current", "POST /api/companies", "PUT /api/companies/{id}", "DELETE /api/companies/{id}",
        "GET /api/users", "GET /api/users/{id}", "POST /api/users", "PUT /api/users/{id}", "DELETE /api/users/{id}",
        "GET /api/stocks", "GET /api/stocks/{id}", "POST /api/stocks", "PUT /api/stocks/{id}", "DELETE /api/stocks/{id}", "GET /api/stocks/{id}/transactions",
        "GET /api/current-accounts", "GET /api/current-accounts/{id}", "POST /api/current-accounts", "PUT /api/current-accounts/{id}", "DELETE /api/current-accounts/{id}", "GET /api/current-accounts/{id}/transactions",
        "GET /api/invoices", "GET /api/invoices/{id}", "POST /api/invoices", "PUT /api/invoices/{id}", "DELETE /api/invoices/{id}", "POST /api/invoices/{id}/approve", "POST /api/invoices/{id}/cancel",
        "GET /api/stock-trans", "GET /api/act-trans", "GET /api/dashboard/summary",
        "GET /api/me/account", "GET /api/me/transactions", "GET /api/me/invoices", "GET /api/me/invoices/{id}",
        "POST /api/admin/reconciliation/run",
    };

    private static JsonNode? doc;

    private async Task<JsonNode> DocAsync()
    {
        if (doc is null)
        {
            using var res = await fx.NewSession().Client.GetAsync("/swagger/v1/swagger.json");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            doc = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        }

        return doc;
    }

    [Theory]
    [MemberData(nameof(ContractOperations))]
    public async Task Operation_documented_with_success_response_and_auth(string op)
    {
        var d = await DocAsync();
        var (method, path) = (op.Split(' ')[0].ToLowerInvariant(), op.Split(' ')[1]);
        var o = d["paths"]?[path]?[method];
        Assert.True(o is not null, $"{op} missing in swagger");
        var responses = o!["responses"]!.AsObject();
        Assert.True(responses.Any(r => r.Key.StartsWith('2')), $"{op}: no 2xx response ({string.Join(",", responses.Select(r => r.Key))})");

        var security = o["security"] ?? d["security"];
        var secured = security is JsonArray { Count: > 0 };
        if (Anonymous.Contains(op))
        {
            Assert.False(secured, $"{op} is anonymous but documents a security requirement");
        }
        else
        {
            Assert.True(secured, $"{op} requires a bearer token but has no security requirement");
            Assert.True(responses.ContainsKey("401"), $"{op}: 401 not documented");
        }
    }

    [Fact]
    public async Task Bearer_scheme_and_typed_success_schemas()
    {
        var d = await DocAsync();
        Assert.Contains("bearer", d["components"]!["securitySchemes"]!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        var ok = d["paths"]!["/api/stocks/{id}"]!["get"]!["responses"]!["200"]!;
        Assert.Contains("StockDto", ok.ToJsonString());
        var approve = d["paths"]!["/api/invoices/{id}/approve"]!["post"]!["responses"]!["200"]!;
        Assert.Contains("InvoiceDto", approve.ToJsonString());
    }
}
