using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>
/// Regression tests for BUG-1 and BUG-2 (docs/TEST_REPORT.md "Bugs"), fixed by Backend and no longer skipped.
/// SkippableFact (RUN_KNOWN_BUGS=1) stays available for future open bugs.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KnownBugTests(ApiFixture fx)
{
    private const string Bug1 = "BUG-1 (Backend): GET /api/companies/current returns 403 for operators (class-level AdminOnly + action Operator are AND-combined)";
    private const string Bug2 = "BUG-2 (Backend): model-binding/JSON 400s are application/json with English messages instead of application/problem+json";

    [Fact]
    public async Task Operator_companies_current_returns_own_company()
    {
        var op = await fx.OperatorAsync();
        var c = await op.GetJsonAsync("/api/companies/current");
        Assert.Equal(fx.DemoCompanyId, c["id"].Int());
    }

    [Fact]
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
