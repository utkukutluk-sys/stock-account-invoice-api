using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §3.8, §11.2 #3: company B operator cannot see Demo data (404), admin X-Company-Id filtering, COMPANY_REQUIRED.</summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationTests(ApiFixture fx)
{
    private async Task<(int CompanyId, ApiSession Operator)> CreateCompanyWithOperatorAsync()
    {
        var admin = await fx.AdminAsync();
        var company = await admin.PostJsonAsync("/api/companies", new { name = "IT Firma " + TestData.Code(), taxNumber = TestData.TaxNumber(), city = "İzmir", isActive = true }, HttpStatusCode.Created);
        var email = $"opb.{TestData.Code().ToLowerInvariant()}@test.local";
        await admin.PostJsonAsync("/api/users", new { email, fullName = "Operator B", password = "Passw0rdB", role = "User", companyId = company["id"].Int(), isActive = true }, HttpStatusCode.Created);
        return (company["id"].Int(), await fx.LoginAsync(email, "Passw0rdB"));
    }

    [Fact]
    public async Task Operator_of_other_company_gets_404_for_demo_rows_and_sees_only_own_lists()
    {
        var (companyB, opB) = await CreateCompanyWithOperatorAsync();
        var demoOp = await fx.OperatorAsync();
        var elma = (await demoOp.GetJsonAsync("/api/stocks?q=ELMA")).Items().First()!;
        var c001 = (await demoOp.GetJsonAsync("/api/current-accounts?q=C001")).Items().First()!;
        var demoInvoice = (await demoOp.GetJsonAsync("/api/invoices")).Items().First()!;

        await ApiSession.ExpectProblemAsync(await opB.GetAsync($"/api/stocks/{elma["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await opB.GetAsync($"/api/current-accounts/{c001["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await opB.GetAsync($"/api/invoices/{demoInvoice["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await opB.PutAsync($"/api/stocks/{elma["id"].Int()}", TestData.StockBody("ELMA", rowVersion: elma["rowVersion"].Str())), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await opB.GetAsync($"/api/stocks/{elma["id"].Int()}/transactions"), HttpStatusCode.NotFound, "NOT_FOUND");

        // lists empty for B (no stocks yet), then only its own row
        Assert.Equal(0, (await opB.GetJsonAsync("/api/stocks"))["totalCount"].Int());
        Assert.Equal(0, (await opB.GetJsonAsync("/api/invoices"))["totalCount"].Int());
        Assert.Equal(0, (await opB.GetJsonAsync("/api/stock-trans"))["totalCount"].Int());
        Assert.Equal(0, (await opB.GetJsonAsync("/api/act-trans"))["totalCount"].Int());
        var own = await opB.CreateStockAsync("ELMA"); // same code allowed in another company
        Assert.Equal(companyB, own["companyId"].Int());
        var list = await opB.GetJsonAsync("/api/stocks");
        Assert.Equal(1, list["totalCount"].Int());
        Assert.All(list.Items(), s => Assert.Equal(companyB, s!["companyId"].Int()));

        // X-Company-Id is ignored for non-admins
        opB.CompanyId = fx.DemoCompanyId;
        var stillOwn = await opB.GetJsonAsync("/api/stocks");
        Assert.All(stillOwn.Items(), s => Assert.Equal(companyB, s!["companyId"].Int()));
        opB.CompanyId = null;

        // cross-company references in an invoice → 422 INVALID_REFERENCE
        var accB = await opB.CreateAccountAsync("Customer");
        await ApiSession.ExpectProblemAsync(
            await opB.PostAsync("/api/invoices", TestData.InvoiceBody("Sales", accB["id"].Int(), [(elma["id"].Int(), 1, 1, 1)])),
            HttpStatusCode.UnprocessableEntity, "INVALID_REFERENCE");
        await ApiSession.ExpectProblemAsync(
            await opB.PostAsync("/api/invoices", TestData.InvoiceBody("Sales", c001["id"].Int(), [(own["id"].Int(), 1, 1, 1)])),
            HttpStatusCode.UnprocessableEntity, "INVALID_REFERENCE");

        // dashboard counts only B's data
        var dash = await opB.GetJsonAsync("/api/dashboard/summary");
        Assert.Equal(1, dash["stockCount"].Int());
        Assert.Equal(1, dash["currentAccountCount"].Int());
    }

    [Fact]
    public async Task Admin_with_company_header_sees_filtered_lists_and_without_sees_all()
    {
        var (companyB, opB) = await CreateCompanyWithOperatorAsync();
        await opB.CreateStockAsync();

        var admin = await fx.LoginAsync(ApiFixture.AdminEmail, ApiFixture.AdminPassword);
        var all = await admin.GetJsonAsync("/api/stocks?pageSize=100");
        Assert.Contains(all.Items(), s => s!["companyId"].Int() == companyB);
        Assert.Contains(all.Items(), s => s!["companyId"].Int() == fx.DemoCompanyId);

        admin.CompanyId = companyB;
        var onlyB = await admin.GetJsonAsync("/api/stocks?pageSize=100");
        Assert.NotEmpty(onlyB.Items());
        Assert.All(onlyB.Items(), s => Assert.Equal(companyB, s!["companyId"].Int()));
        var current = await admin.GetJsonAsync("/api/companies/current");
        Assert.Equal(companyB, current["id"].Int());

        admin.CompanyId = fx.DemoCompanyId;
        var onlyDemo = await admin.GetJsonAsync("/api/current-accounts?pageSize=100");
        Assert.All(onlyDemo.Items(), s => Assert.Equal(fx.DemoCompanyId, s!["companyId"].Int()));
    }

    [Fact]
    public async Task Admin_create_without_company_is_400_company_required()
    {
        var admin = await fx.AdminAsync();
        Assert.Null(admin.CompanyId);
        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/stocks", TestData.StockBody()), HttpStatusCode.BadRequest, "COMPANY_REQUIRED");
        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/current-accounts", TestData.AccountBody()), HttpStatusCode.BadRequest, "COMPANY_REQUIRED");

        var demoOp = await fx.OperatorAsync();
        var c001 = (await demoOp.GetJsonAsync("/api/current-accounts?q=C001")).Items().First()!;
        var elma = (await demoOp.GetJsonAsync("/api/stocks?q=ELMA")).Items().First()!;
        await ApiSession.ExpectProblemAsync(
            await admin.PostAsync("/api/invoices", TestData.InvoiceBody("Sales", c001["id"].Int(), [(elma["id"].Int(), 1, 1, 1)])),
            HttpStatusCode.BadRequest, "COMPANY_REQUIRED");
        await ApiSession.ExpectProblemAsync(await admin.GetAsync("/api/companies/current"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Admin_non_integer_company_header_is_400()
    {
        var admin = await fx.AdminAsync();
        await ApiSession.ExpectProblemAsync(
            await admin.GetAsync("/api/stocks", r => r.Headers.Add("X-Company-Id", "abc")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }
}
