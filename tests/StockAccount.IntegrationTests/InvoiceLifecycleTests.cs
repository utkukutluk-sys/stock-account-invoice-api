using System.Net;
using System.Text.Json.Nodes;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §3.2–3.5, §11.2 #9–11: Ahmet/Elma, status machine, numbering, negative stock, purchase, cancel, zero total.</summary>
[Collection(ApiCollection.Name)]
public sealed class InvoiceLifecycleTests(ApiFixture fx)
{
    private static async Task<JsonNode> StockByCode(ApiSession s, string code) =>
        (await s.GetJsonAsync($"/api/stocks?q={code}")).Items().Single(x => x!["code"].Str() == code)!;

    private static async Task<JsonNode> AccountByCode(ApiSession s, string code) =>
        (await s.GetJsonAsync($"/api/current-accounts?q={code}")).Items().Single(x => x!["code"].Str() == code)!;

    [Fact]
    public async Task Ahmet_buys_2kg_elma_exact_ledgers_and_balances()
    {
        var op = await fx.OperatorAsync();
        var elma = await StockByCode(op, "ELMA");
        var c001 = await AccountByCode(op, "C001");
        Assert.Equal("Kg", elma["unit"].Str());
        Assert.Equal(1m, elma["vatRate"].Dec());
        var elmaBefore = elma["balance"].Dec();
        var ahmetBefore = c001["balance"].Dec();

        // create Draft
        var draft = await op.CreateInvoiceAsync("Sales", c001["id"].Int(), (elma["id"].Int(), 2m, 50m, 1m));
        Assert.Equal("Draft", draft["status"].Str());
        Assert.Equal(100.00m, draft["subTotal"].Dec());
        Assert.Equal(1.00m, draft["vatTotal"].Dec());
        Assert.Equal(101.00m, draft["grandTotal"].Dec());
        Assert.Null(draft["number"]?.GetValue<int?>());
        var line = draft["lines"]![0]!;
        Assert.Equal(100.00m, line["lineNet"].Dec());
        Assert.Equal(1.00m, line["vatAmount"].Dec());
        Assert.Equal(101.00m, line["lineTotal"].Dec());
        Assert.Equal("ELMA", line["stockCode"].Str());
        Assert.Equal("Kg", line["unit"].Str());

        // drafts do not move balances or create ledger rows
        Assert.Equal(elmaBefore, (await op.GetJsonAsync($"/api/stocks/{elma["id"].Int()}"))["balance"].Dec());
        var invoiceId = draft["id"].Int();
        Assert.Equal(0, (await op.GetJsonAsync($"/api/stock-trans?invoiceId={invoiceId}"))["totalCount"].Int());

        // approve
        var approved = await op.ApproveAsync(draft);
        Assert.Equal("Approved", approved["status"].Str());
        Assert.Matches(@"^SAT\d{6}$", approved["documentNo"].Str());
        Assert.Equal(approved["documentNo"].Str(), "SAT" + approved["number"].Int().ToString("D6"));
        Assert.NotNull(approved["approvedAt"]?.GetValue<string>());

        // exact StockTrans: one Out 2.000 row for ELMA
        var st = await op.GetJsonAsync($"/api/stock-trans?invoiceId={invoiceId}");
        var stRow = Assert.Single(st.Items())!;
        Assert.Equal(elma["id"].Int(), stRow["stockId"].Int());
        Assert.Equal("Out", stRow["direction"].Str());
        Assert.Equal(2.000m, stRow["quantity"].Dec());
        Assert.Equal(50m, stRow["unitPrice"].Dec());
        Assert.Equal(100.00m, stRow["amount"].Dec());
        Assert.False(stRow["isReversal"]!.GetValue<bool>());
        Assert.Equal(approved["documentNo"].Str(), stRow["documentNo"].Str());
        Assert.Equal(TestData.Today, stRow["transDate"].Str());
        Assert.StartsWith(approved["documentNo"].Str(), stRow["description"].Str());

        // exact ActTrans: one Debit 101.00 row for C001
        var at = await op.GetJsonAsync($"/api/act-trans?invoiceId={invoiceId}");
        var atRow = Assert.Single(at.Items())!;
        Assert.Equal(c001["id"].Int(), atRow["currentAccountId"].Int());
        Assert.Equal("Debit", atRow["direction"].Str());
        Assert.Equal(101.00m, atRow["amount"].Dec());
        Assert.False(atRow["isReversal"]!.GetValue<bool>());

        // DB-level: exactly these rows
        var dbSt = await fx.Factory.QueryAsync($"SELECT StockId, Direction, Quantity, Amount, IsReversal, CompanyId FROM StockTrans WHERE InvoiceId = {invoiceId}");
        var dbStRow = Assert.Single(dbSt);
        Assert.Equal(2, Convert.ToInt32(dbStRow["Direction"])); // Out
        Assert.Equal(2.000m, (decimal)dbStRow["Quantity"]!);
        Assert.Equal(fx.DemoCompanyId, (int)dbStRow["CompanyId"]!);
        var dbAt = await fx.Factory.QueryAsync($"SELECT CurrentAccountId, Direction, Amount FROM ActTrans WHERE InvoiceId = {invoiceId}");
        var dbAtRow = Assert.Single(dbAt);
        Assert.Equal(1, Convert.ToInt32(dbAtRow["Direction"])); // Debit
        Assert.Equal(101.00m, (decimal)dbAtRow["Amount"]!);

        // stored balances (API + DB)
        var elmaAfter = await op.GetJsonAsync($"/api/stocks/{elma["id"].Int()}");
        var ahmetAfter = await op.GetJsonAsync($"/api/current-accounts/{c001["id"].Int()}");
        Assert.Equal(elmaBefore - 2m, elmaAfter["balance"].Dec());
        Assert.Equal(ahmetBefore + 101.00m, ahmetAfter["balance"].Dec());
        Assert.Equal(elmaBefore - 2m, await fx.Factory.ScalarAsync<decimal>($"SELECT Balance FROM Stocks WHERE Id = {elma["id"].Int()}"));
        Assert.Equal(ahmetBefore + 101m, await fx.Factory.ScalarAsync<decimal>($"SELECT Balance FROM CurrentAccounts WHERE Id = {c001["id"].Int()}"));

        // stock/account transaction sub-resources show the rows too
        var sub = await op.GetJsonAsync($"/api/stocks/{elma["id"].Int()}/transactions");
        Assert.Equal(invoiceId, sub.Items()[0]!["invoiceId"].Int()); // default sort -transDate,-id → newest first
        var accSub = await op.GetJsonAsync($"/api/current-accounts/{c001["id"].Int()}/transactions?direction=Debit");
        Assert.Contains(accSub.Items(), r => r!["invoiceId"].Int() == invoiceId);

        // portal user Ahmet sees the invoice and his own balance
        var portal = await fx.PortalAsync();
        var myAcc = await portal.GetJsonAsync("/api/me/account");
        Assert.Equal(ahmetBefore + 101.00m, myAcc["balance"].Dec());
        var myInv = await portal.GetJsonAsync($"/api/me/invoices/{invoiceId}");
        Assert.Equal(approved["documentNo"].Str(), myInv["documentNo"].Str());
        Assert.Contains((await portal.GetJsonAsync("/api/me/transactions")).Items(), r => r!["invoiceId"].Int() == invoiceId);

        // ---- rules on an approved invoice
        await ApiSession.ExpectProblemAsync(await op.PutAsync($"/api/invoices/{invoiceId}", TestData.InvoiceBody("Sales", c001["id"].Int(), [(elma["id"].Int(), 1, 1, 1)], rowVersion: approved["rowVersion"].Str())), HttpStatusCode.Conflict, "INVOICE_NOT_DRAFT");
        await ApiSession.ExpectProblemAsync(await op.DeleteAsync($"/api/invoices/{invoiceId}"), HttpStatusCode.Conflict, "INVOICE_NOT_DRAFT");
        await ApiSession.ExpectProblemAsync(await op.ApproveRawAsync(approved), HttpStatusCode.Conflict, "INVOICE_NOT_DRAFT");
        await ApiSession.ExpectProblemAsync(await op.CancelRawAsync(approved), HttpStatusCode.Forbidden, "FORBIDDEN");

        // ---- cancel as admin → reversal rows, balances restored
        var admin = await fx.AdminDemoAsync();
        await ApiSession.ExpectProblemAsync(await admin.CancelRawAsync(approved, "ab"), HttpStatusCode.BadRequest, "VALIDATION_FAILED"); // reason 3..500
        var cancelled = await ApiSession.ExpectJsonAsync(await admin.CancelRawAsync(approved, "Müşteri iade etti"), HttpStatusCode.OK);
        Assert.Equal("Cancelled", cancelled["status"].Str());
        Assert.Equal("Müşteri iade etti", cancelled["cancelReason"].Str());
        Assert.Equal(approved["documentNo"].Str(), cancelled["documentNo"].Str());

        var st2 = (await op.GetJsonAsync($"/api/stock-trans?invoiceId={invoiceId}&sort=createdAt")).Items();
        Assert.Equal(2, st2.Count);
        var rev = st2.Single(r => r!["isReversal"]!.GetValue<bool>())!;
        Assert.Equal("In", rev["direction"].Str());
        Assert.Equal(2.000m, rev["quantity"].Dec());
        var at2 = (await op.GetJsonAsync($"/api/act-trans?invoiceId={invoiceId}")).Items();
        Assert.Equal(2, at2.Count);
        var arev = at2.Single(r => r!["isReversal"]!.GetValue<bool>())!;
        Assert.Equal("Credit", arev["direction"].Str());
        Assert.Equal(101.00m, arev["amount"].Dec());

        Assert.Equal(elmaBefore, (await op.GetJsonAsync($"/api/stocks/{elma["id"].Int()}"))["balance"].Dec());
        Assert.Equal(ahmetBefore, (await op.GetJsonAsync($"/api/current-accounts/{c001["id"].Int()}"))["balance"].Dec());

        // cancel twice → 409 INVOICE_NOT_APPROVED
        await ApiSession.ExpectProblemAsync(await admin.CancelRawAsync(cancelled), HttpStatusCode.Conflict, "INVOICE_NOT_APPROVED");
        // original rows untouched (immutable ledger)
        Assert.Equal(1, await fx.Factory.ScalarAsync<int>($"SELECT COUNT(*) FROM StockTrans WHERE InvoiceId = {invoiceId} AND IsReversal = 0 AND Direction = 2"));
    }

    [Fact]
    public async Task Numbering_is_sequential_per_series_and_cancel_of_draft_is_rejected()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 100);
        var acc = await op.CreateAccountAsync("Customer");

        var series = "Q" + Random.Shared.Next(100000, 999999);
        var docs = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var d = await op.PostJsonAsync("/api/invoices", TestData.InvoiceBody("Sales", acc["id"].Int(), [(stock["id"].Int(), 1, 1, 1)], series: series), HttpStatusCode.Created);
            if (i == 1)
            {
                // a draft that is deleted must not consume a number
                using var _ = await op.DeleteAsync($"/api/invoices/{d["id"].Int()}");
                continue;
            }

            var a = await op.ApproveAsync(d);
            docs.Add(a["number"].Int());
            Assert.Equal(series + a["number"].Int().ToString("D6"), a["documentNo"].Str());
        }

        Assert.Equal(new[] { 1, 2 }, docs.ToArray());

        // default SAT series continues n, n+1
        var s1 = await op.ApproveAsync(await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 1, 1, 1)));
        var s2 = await op.ApproveAsync(await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 1, 1, 1)));
        Assert.Equal(s1["number"].Int() + 1, s2["number"].Int());
        Assert.StartsWith("SAT", s2["documentNo"].Str());

        // draft cannot be cancelled
        var draft = await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 1, 1, 1));
        var admin = await fx.AdminDemoAsync();
        await ApiSession.ExpectProblemAsync(await admin.CancelRawAsync(draft), HttpStatusCode.Conflict, "INVOICE_NOT_APPROVED");
    }

    [Fact]
    public async Task Purchase_numbering_uses_ALS_and_moves_stock_in_account_credit()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync(vatRate: 20);
        var supplier = await op.CreateAccountAsync("Supplier");
        var inv = await op.CreateInvoiceAsync("Purchase", supplier["id"].Int(), (stock["id"].Int(), 10.5m, 4m, 20m));
        Assert.Equal("ALS", inv["series"].Str());
        Assert.Equal(42.00m, inv["subTotal"].Dec());
        Assert.Equal(8.40m, inv["vatTotal"].Dec());
        Assert.Equal(50.40m, inv["grandTotal"].Dec());
        var a = await op.ApproveAsync(inv);
        Assert.Matches(@"^ALS\d{6}$", a["documentNo"].Str());
        Assert.True(a["number"].Int() >= 2); // ALS000001 is the seed opening purchase

        var st = Assert.Single((await op.GetJsonAsync($"/api/stock-trans?invoiceId={a["id"].Int()}")).Items())!;
        Assert.Equal("In", st["direction"].Str());
        Assert.Equal(10.5m, st["quantity"].Dec());
        var at = Assert.Single((await op.GetJsonAsync($"/api/act-trans?invoiceId={a["id"].Int()}")).Items())!;
        Assert.Equal("Credit", at["direction"].Str());
        Assert.Equal(50.40m, at["amount"].Dec());

        Assert.Equal(10.5m, (await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}"))["balance"].Dec());
        Assert.Equal(-50.40m, (await op.GetJsonAsync($"/api/current-accounts/{supplier["id"].Int()}"))["balance"].Dec());
    }

    [Fact]
    public async Task Insufficient_stock_on_sale_and_on_cancel_of_purchase()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        var purchase = await op.PurchaseAsync(stock["id"].Int(), 5);
        var acc = await op.CreateAccountAsync("Customer");

        // two lines of the same stock summing above balance → 422 listing the code
        var draft = await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 3m, 1m, 1m), (stock["id"].Int(), 3m, 1m, 1m));
        var p = await ApiSession.ExpectProblemAsync(await op.ApproveRawAsync(draft), HttpStatusCode.UnprocessableEntity, "INSUFFICIENT_STOCK");
        Assert.Contains(stock["code"].Str(), p["detail"].Str());
        // nothing posted, still Draft
        var still = await op.GetJsonAsync($"/api/invoices/{draft["id"].Int()}");
        Assert.Equal("Draft", still["status"].Str());
        Assert.Equal(5m, (await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}"))["balance"].Dec());
        Assert.Equal(0, (await op.GetJsonAsync($"/api/stock-trans?invoiceId={draft["id"].Int()}"))["totalCount"].Int());

        // exact balance is allowed (5 - 5 = 0)
        var ok = await op.ApproveAsync(await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 5m, 1m, 1m)));
        Assert.Equal("Approved", ok["status"].Str());
        Assert.Equal(0m, (await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}"))["balance"].Dec());

        // cancelling the purchase would make the stock negative → 422
        var admin = await fx.AdminDemoAsync();
        var fresh = await admin.GetJsonAsync($"/api/invoices/{purchase["id"].Int()}");
        await ApiSession.ExpectProblemAsync(await admin.CancelRawAsync(fresh), HttpStatusCode.UnprocessableEntity, "INSUFFICIENT_STOCK");
    }

    [Fact]
    public async Task Approve_rules_no_lines_inactive_and_type_mismatch()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 10);
        var acc = await op.CreateAccountAsync("Customer");
        var draft = await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 1m, 1m, 1m));

        // deactivate account after the draft was created → approve 422 INACTIVE_ACCOUNT
        var a = await op.GetJsonAsync($"/api/current-accounts/{acc["id"].Int()}");
        await op.PutJsonAsync($"/api/current-accounts/{acc["id"].Int()}", new { code = a["code"].Str(), title = a["title"].Str(), type = "Customer", isActive = false, rowVersion = a["rowVersion"].Str() });
        await ApiSession.ExpectProblemAsync(await op.ApproveRawAsync(draft), HttpStatusCode.UnprocessableEntity, "INACTIVE_ACCOUNT");

        // reactivate, but change type to Supplier → ACCOUNT_TYPE_MISMATCH
        a = await op.GetJsonAsync($"/api/current-accounts/{acc["id"].Int()}");
        await op.PutJsonAsync($"/api/current-accounts/{acc["id"].Int()}", new { code = a["code"].Str(), title = a["title"].Str(), type = "Supplier", isActive = true, rowVersion = a["rowVersion"].Str() });
        await ApiSession.ExpectProblemAsync(await op.ApproveRawAsync(draft), HttpStatusCode.UnprocessableEntity, "ACCOUNT_TYPE_MISMATCH");

        // back to Customer, deactivate the stock → INACTIVE_STOCK
        a = await op.GetJsonAsync($"/api/current-accounts/{acc["id"].Int()}");
        await op.PutJsonAsync($"/api/current-accounts/{acc["id"].Int()}", new { code = a["code"].Str(), title = a["title"].Str(), type = "Customer", isActive = true, rowVersion = a["rowVersion"].Str() });
        var s = await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}");
        await op.PutJsonAsync($"/api/stocks/{stock["id"].Int()}", new { code = s["code"].Str(), name = s["name"].Str(), unit = "Kg", vatRate = 1, unitPrice = 1, isActive = false, rowVersion = s["rowVersion"].Str() });
        await ApiSession.ExpectProblemAsync(await op.ApproveRawAsync(draft), HttpStatusCode.UnprocessableEntity, "INACTIVE_STOCK");

        // INVOICE_HAS_NO_LINES: the API never lets a draft be saved without lines (400), so force it at DB level.
        await fx.Factory.ExecuteSqlAsync($"DELETE FROM InvoiceLines WHERE InvoiceId = {draft["id"].Int()}");
        var fresh = await op.GetJsonAsync($"/api/invoices/{draft["id"].Int()}");
        await ApiSession.ExpectProblemAsync(await op.ApproveRawAsync(fresh), HttpStatusCode.UnprocessableEntity, "INVOICE_HAS_NO_LINES");
    }

    [Fact]
    public async Task Zero_total_invoice_posts_stock_but_no_act_trans()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        var supplier = await op.CreateAccountAsync("Supplier");
        var inv = await op.CreateInvoiceAsync("Purchase", supplier["id"].Int(), (stock["id"].Int(), 3m, 0m, 20m));
        Assert.Equal(0m, inv["grandTotal"].Dec());
        var a = await op.ApproveAsync(inv);
        Assert.Equal("Approved", a["status"].Str());
        Assert.Single((await op.GetJsonAsync($"/api/stock-trans?invoiceId={a["id"].Int()}")).Items());
        Assert.Equal(0, (await op.GetJsonAsync($"/api/act-trans?invoiceId={a["id"].Int()}"))["totalCount"].Int());
        Assert.Equal(3m, (await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}"))["balance"].Dec());
        Assert.Equal(0m, (await op.GetJsonAsync($"/api/current-accounts/{supplier["id"].Int()}"))["balance"].Dec());

        // cancel → stock reversal only, still no ActTrans
        var admin = await fx.AdminDemoAsync();
        var c = await ApiSession.ExpectJsonAsync(await admin.CancelRawAsync(a), HttpStatusCode.OK);
        Assert.Equal("Cancelled", c["status"].Str());
        Assert.Equal(2, (await op.GetJsonAsync($"/api/stock-trans?invoiceId={a["id"].Int()}"))["totalCount"].Int());
        Assert.Equal(0, (await op.GetJsonAsync($"/api/act-trans?invoiceId={a["id"].Int()}"))["totalCount"].Int());
        Assert.Equal(0m, (await op.GetJsonAsync($"/api/stocks/{stock["id"].Int()}"))["balance"].Dec());
    }

    [Fact]
    public async Task Stale_rowversion_on_approve_is_409_and_invoice_stays_draft()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync();
        await op.PurchaseAsync(stock["id"].Int(), 10);
        var acc = await op.CreateAccountAsync("Customer");
        var draft = await op.CreateInvoiceAsync("Sales", acc["id"].Int(), (stock["id"].Int(), 1m, 1m, 1m));
        var updated = await op.PutJsonAsync($"/api/invoices/{draft["id"].Int()}", TestData.InvoiceBody("Sales", acc["id"].Int(), [(stock["id"].Int(), 2, 1, 1)], rowVersion: draft["rowVersion"].Str()));

        await ApiSession.ExpectProblemAsync(await op.ApproveRawAsync(draft), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await ApiSession.ExpectProblemAsync(await op.PostAsync($"/api/invoices/{draft["id"].Int()}/approve", new { }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await op.PostAsync($"/api/invoices/{draft["id"].Int()}/approve", new { rowVersion = "%%%notbase64" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal("Draft", (await op.GetJsonAsync($"/api/invoices/{draft["id"].Int()}"))["status"].Str());
        var ok = await op.ApproveAsync(updated);
        Assert.Equal(2m, ok["lines"]![0]!["quantity"].Dec());
    }

    [Fact]
    public async Task Dashboard_summary_shape_and_values()
    {
        var op = await fx.OperatorAsync();
        var d = await op.GetJsonAsync("/api/dashboard/summary");
        foreach (var k in new[] { "stockCount", "currentAccountCount", "draftInvoiceCount", "approvedInvoiceCountThisMonth", "salesTotalThisMonth", "purchaseTotalThisMonth", "totalReceivable", "totalPayable", "lowestStocks", "recentInvoices" })
        {
            Assert.True(d.AsObject().ContainsKey(k), k);
        }

        Assert.True(d["stockCount"].Int() >= 5);
        Assert.True(d["totalPayable"].Dec() >= 18714m); // seed S001
        Assert.True(d["lowestStocks"]!.AsArray().Count <= 5);
        Assert.True(d["recentInvoices"]!.AsArray().Count <= 5);
        var lows = d["lowestStocks"]!.AsArray().Select(x => x!["balance"].Dec()).ToList();
        Assert.Equal(lows.OrderBy(x => x).ToList(), lows);
    }
}
