using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §4.2 policy matrix, §11.2 #2: anonymous 401, wrong policy 403 (ProblemDetails FORBIDDEN).</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthorizationMatrixTests(ApiFixture fx)
{
    // Expected outcome per role: "ok" = not 401/403 (2xx/4xx business), "403" = forbidden.
    public static TheoryData<string, string, string, string, string> Matrix => new()
    {
        // method, url, admin, operator, portal
        { "GET", "/api/companies", "ok", "403", "403" },
        { "GET", "/api/companies/current", "ok", "ok", "403" }, // BUG-1 fixed
        { "POST", "/api/companies", "ok", "403", "403" },
        { "GET", "/api/users", "ok", "403", "403" },
        { "POST", "/api/users", "ok", "403", "403" },
        { "GET", "/api/stocks", "ok", "ok", "403" },
        { "POST", "/api/stocks", "ok", "ok", "403" },
        { "DELETE", "/api/stocks/999999", "ok", "403", "403" },
        { "GET", "/api/stocks/1/transactions", "ok", "ok", "403" },
        { "GET", "/api/current-accounts", "ok", "ok", "403" },
        { "DELETE", "/api/current-accounts/999999", "ok", "403", "403" },
        { "GET", "/api/invoices", "ok", "ok", "403" },
        { "POST", "/api/invoices/999999/approve", "ok", "ok", "403" },
        { "POST", "/api/invoices/999999/cancel", "ok", "403", "403" },
        { "GET", "/api/stock-trans", "ok", "ok", "403" },
        { "GET", "/api/act-trans", "ok", "ok", "403" },
        { "GET", "/api/dashboard/summary", "ok", "ok", "403" },
        { "GET", "/api/me/account", "403", "403", "ok" },
        { "GET", "/api/me/transactions", "403", "403", "ok" },
        { "GET", "/api/me/invoices", "403", "403", "ok" },
        { "POST", "/api/admin/reconciliation/run", "ok", "403", "403" },
        { "GET", "/api/auth/me", "ok", "ok", "ok" },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Policy_matrix(string method, string url, string admin, string op, string portal)
    {
        var body = method is "POST" or "PUT" ? new { } : null;

        using (var anon = await fx.NewSession().SendAsync(new HttpMethod(method), url, body, auth: false))
        {
            await ApiSession.ExpectProblemAsync(anon, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        }

        await Check(await fx.AdminAsync(), admin);
        await Check(await fx.OperatorAsync(), op);
        await Check(await fx.PortalAsync(), portal);

        async Task Check(ApiSession s, string expected)
        {
            if (expected == "any")
            {
                return;
            }

            var res = await s.SendAsync(new HttpMethod(method), url, body);
            if (expected == "403")
            {
                await ApiSession.ExpectProblemAsync(res, HttpStatusCode.Forbidden, "FORBIDDEN");
            }
            else
            {
                using (res)
                {
                    var text = await res.Content.ReadAsStringAsync();
                    Assert.True(res.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                        $"{method} {url} as {s.User?["email"]}: expected access but got {(int)res.StatusCode} {text}");
                }
            }
        }
    }
}
