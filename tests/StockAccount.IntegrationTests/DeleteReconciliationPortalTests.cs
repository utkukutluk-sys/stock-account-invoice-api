using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §3.7 delete rules (#12), §3.9 reconciliation (#13), portal /me/* scoping.</summary>
[Collection(ApiCollection.Name)]
public sealed class DeleteReconciliationPortalTests(ApiFixture fx)
{
    [Fact]
    public async Task Stock_delete_rules()
    {
        var op = await fx.OperatorAsync();
        var admin = await fx.AdminDemoAsync();

        var withBalance = await op.CreateStockAsync();
        await op.PurchaseAsync(withBalance["id"].Int(), 2);
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/stocks/{withBalance["id"].Int()}"), HttpStatusCode.Conflict, "STOCK_HAS_BALANCE");

        var inDraft = await op.CreateStockAsync();
        var acc = await op.CreateAccountAsync("Customer");
        var draft = await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (inDraft["id"].Int(), 1m, 1m, 1m));
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/stocks/{inDraft["id"].Int()}"), HttpStatusCode.Conflict, "STOCK_IN_USE");
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/current-accounts/{acc["id"].Int()}"), HttpStatusCode.Conflict, "ACCOUNT_IN_USE");

        using (var delDraft = await op.DeleteAsync($"/api/invoices/{draft["id"].Int()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delDraft.StatusCode);
        }

        using (var ok = await admin.DeleteAsync($"/api/stocks/{inDraft["id"].Int()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }

        await ApiSession.ExpectProblemAsync(await op.GetAsync($"/api/stocks/{inDraft["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/stocks/{inDraft["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Account_delete_rules_and_history_survives_soft_delete()
    {
        var op = await fx.OperatorAsync();
        var admin = await fx.AdminDemoAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 5);
        var acc = await op.CreateAccountAsync("Customer");
        var sale = await op.ApproveAsync(await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 5m, 2m, 0m)));
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/current-accounts/{acc["id"].Int()}"), HttpStatusCode.Conflict, "ACCOUNT_HAS_BALANCE");

        // stock now has balance 0 and no drafts → can be soft-deleted; history (invoice, ledgers) stays visible
        using (var del = await admin.DeleteAsync($"/api/stocks/{stock["id"].Int()}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        }

        var inv = await op.GetJsonAsync($"/api/invoices/{sale["id"].Int()}");
        Assert.Equal(stock["code"].Str(), inv["lines"]![0]!["stockCode"].Str());
        Assert.Equal(1, (await op.GetJsonAsync($"/api/stock-trans?invoiceId={sale["id"].Int()}"))["totalCount"].Int());
        Assert.Contains((await op.GetJsonAsync($"/api/invoices?currentAccountId={acc["id"].Int()}")).Items(), i => i!["id"].Int() == sale["id"].Int());
    }

    [Fact]
    public async Task Reconciliation_detects_tampering_and_fixes()
    {
        var admin = await fx.AdminAsync();
        var clean = await admin.PostJsonAsync("/api/admin/reconciliation/run?fix=false", null, HttpStatusCode.OK);
        Assert.Empty(clean["discrepancies"]!.AsArray());
        Assert.True(clean["stocksChecked"].Int() >= 5);
        Assert.True(clean["accountsChecked"].Int() >= 4);
        Assert.False(clean["fixed"]!.GetValue<bool>());

        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 7);
        await fx.Factory.ExecuteSqlAsync($"UPDATE Stocks SET Balance = Balance + 3 WHERE Id = {stock["id"].Int()}");

        var found = await admin.PostJsonAsync("/api/admin/reconciliation/run?fix=false", null, HttpStatusCode.OK);
        var d = Assert.Single(found["discrepancies"]!.AsArray())!;
        Assert.Equal("Stock", d["entityType"].Str());
        Assert.Equal(stock["id"].Int(), d["entityId"].Int());
        Assert.Equal(stock["code"].Str(), d["code"].Str());
        Assert.Equal(10m, d["storedBalance"].Dec());
        Assert.Equal(7m, d["ledgerBalance"].Dec());
        Assert.Equal(3m, d["difference"].Dec());
        Assert.Equal(fx.DemoCompanyId, d["companyId"].Int());

        var fixedRun = await admin.PostJsonAsync("/api/admin/reconciliation/run?fix=true", null, HttpStatusCode.OK);
        Assert.True(fixedRun["fixed"]!.GetValue<bool>());
        Assert.Single(fixedRun["discrepancies"]!.AsArray());
        Assert.Equal(7m, await fx.Factory.ScalarAsync<decimal>($"SELECT Balance FROM Stocks WHERE Id = {stock["id"].Int()}"));
        // API (incl. cache) reflects the fix
        Assert.Equal(7m, (await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}"))["balance"].Dec());

        var after = await admin.PostJsonAsync("/api/admin/reconciliation/run", null, HttpStatusCode.OK);
        Assert.Empty(after["discrepancies"]!.AsArray());

        // account tampering too
        var acc = await op.CreateAccountAsync();
        await fx.Factory.ExecuteSqlAsync($"UPDATE CurrentAccounts SET Balance = -5 WHERE Id = {acc["id"].Int()}");
        var accRun = await admin.PostJsonAsync("/api/admin/reconciliation/run?fix=true", null, HttpStatusCode.OK);
        var ad = Assert.Single(accRun["discrepancies"]!.AsArray())!;
        Assert.Equal("CurrentAccount", ad["entityType"].Str());
        Assert.Equal(0m, await fx.Factory.ScalarAsync<decimal>($"SELECT Balance FROM CurrentAccounts WHERE Id = {acc["id"].Int()}"));
    }

    [Fact]
    public async Task Ledgers_are_immutable_at_db_level()
    {
        var ex = await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => fx.Factory.ExecuteSqlAsync("UPDATE TOP (1) StockTrans SET Quantity = Quantity + 1"));
        Assert.Contains("50001", ex.Number + " " + ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => fx.Factory.ExecuteSqlAsync("DELETE TOP (1) FROM ActTrans"));
    }

    [Fact]
    public async Task Portal_sees_only_own_approved_data()
    {
        var op = await fx.OperatorAsync();
        var portal = await fx.PortalAsync();
        var me = await portal.GetJsonAsync("/api/me/account");
        Assert.Equal("C001", me["code"].Str());
        var ahmetId = me["id"].Int();

        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 10);
        var other = await op.CreateAccountAsync("Customer");
        var otherInvoice = await op.ApproveAsync(await op.CreateInvoiceAsync("Sales", other["id"].Int(), (stock["id"].Int(), 1m, 1m, 1m)));
        var ownDraft = await op.CreateInvoiceAsync("Sales", ahmetId, (stock["id"].Int(), 1m, 1m, 1m));

        await ApiSession.ExpectProblemAsync(await portal.GetAsync($"/api/me/invoices/{otherInvoice["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await portal.GetAsync($"/api/me/invoices/{ownDraft["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");

        var invoices = await portal.GetJsonAsync("/api/me/invoices?pageSize=100");
        Assert.All(invoices.Items(), i =>
        {
            Assert.Equal(ahmetId, i!["currentAccountId"].Int());
            Assert.NotEqual("Draft", i["status"].Str());
        });
        // status=Draft is rejected with 400 (only Approved/Cancelled allowed) - acceptable reading of "only Approved/Cancelled are ever returned"
        using (var draftFilter = await portal.GetAsync("/api/me/invoices?status=Draft"))
        {
            Assert.True(draftFilter.StatusCode == HttpStatusCode.BadRequest || (await draftFilter.Content.ReadAsStringAsync()).Contains("\"totalCount\":0"));
        }

        var trans = await portal.GetJsonAsync("/api/me/transactions?pageSize=100");
        Assert.All(trans.Items(), t => Assert.Equal(ahmetId, t!["currentAccountId"].Int()));

        // operator endpoints closed for portal
        await ApiSession.ExpectProblemAsync(await portal.GetAsync($"/api/invoices/{otherInvoice["id"].Int()}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ApiSession.ExpectProblemAsync(await portal.GetAsync($"/api/current-accounts/{ahmetId}"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // cleanup the own draft so Ahmet's account stays deletable-agnostic
        using var _ = await op.DeleteAsync($"/api/invoices/{ownDraft["id"].Int()}");
    }
}
