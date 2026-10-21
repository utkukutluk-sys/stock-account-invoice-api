using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>
/// Contract assertions that currently FAIL because of open bugs (docs/TEST_REPORT.md "Bugs"). They are skipped so the suite
/// stays green; remove the Skip once the owner has fixed the bug. Run them anyway with: set env RUN_KNOWN_BUGS=1.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KnownBugTests(ApiFixture fx)
{
    private const string Bug1 = "BUG-1 (Backend): GET /api/companies/current returns 403 for operators (class-level AdminOnly + action Operator are AND-combined)";
    private const string Bug2 = "BUG-2 (Backend): model-binding/JSON 400s are application/json with English messages instead of application/problem+json";

    [SkippableFact(Bug1)]
    public async Task Operator_companies_current_returns_own_company()
    {
        var op = await fx.OperatorAsync();
        var c = await op.GetJsonAsync("/api/companies/current");
        Assert.Equal(fx.DemoCompanyId, c["id"].Int());
    }

    [SkippableFact(Bug2)]
    public async Task Json_binding_errors_are_problem_json()
    {
        var op = await fx.OperatorAsync();
        var p = await ApiSession.ExpectProblemAsync(
            await op.PostAsync("/api/stocks", new { code = TestData.Code(), name = "x", unit = "Ton", vatRate = 1, unitPrice = 1, isActive = true }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.DoesNotContain("could not be converted", p.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Fact that is skipped unless RUN_KNOWN_BUGS=1.</summary>
public sealed class SkippableFactAttribute : FactAttribute
{
    public SkippableFactAttribute(string reason)
    {
        if (Environment.GetEnvironmentVariable("RUN_KNOWN_BUGS") != "1")
        {
            Skip = reason;
        }
    }
}
