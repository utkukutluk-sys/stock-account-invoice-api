using System.Net;
using StockAccount.IntegrationTests.Infrastructure;

namespace StockAccount.IntegrationTests;

/// <summary>CONTRACT §11.2 #4: CRUD + validation for companies, users, stocks, current accounts, invoices.</summary>
[Collection(ApiCollection.Name)]
public sealed class CrudTests(ApiFixture fx)
{
    [Fact]
    public async Task Company_crud_and_rules()
    {
        var admin = await fx.AdminAsync();
        var tax = TestData.TaxNumber();
        using var createRes = await admin.PostAsync("/api/companies", new { name = "CRUD Firma", taxNumber = tax, taxOffice = "Kadıköy", city = "Bursa", email = "a@b.com", isActive = true });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        Assert.NotNull(createRes.Headers.Location);
        var created = await ApiSession.ExpectJsonAsync(createRes, HttpStatusCode.Created);
        var id = created["id"].Int();

        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/companies", new { name = "Dup", taxNumber = tax, isActive = true }), HttpStatusCode.Conflict, "DUPLICATE_TAX_NUMBER");
        var invalid = await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/companies", new { name = "", taxNumber = "12", email = "nope", isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(invalid["errors"]!.AsObject().ContainsKey("taxNumber"));
        Assert.True(invalid["errors"]!.AsObject().ContainsKey("name"));

        var got = await admin.GetJsonAsync($"/api/companies/{id}");
        Assert.Equal("CRUD Firma", got["name"].Str());

        var updated = await admin.PutJsonAsync($"/api/companies/{id}", new { name = "CRUD Firma 2", taxNumber = tax, city = "Bursa", isActive = true, rowVersion = got["rowVersion"].Str() });
        Assert.Equal("CRUD Firma 2", updated["name"].Str());
        await ApiSession.ExpectProblemAsync(await admin.PutAsync($"/api/companies/{id}", new { name = "X", taxNumber = tax, isActive = true, rowVersion = got["rowVersion"].Str() }), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        await ApiSession.ExpectProblemAsync(await admin.PutAsync($"/api/companies/{id}", new { name = "X", taxNumber = tax, isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var list = await admin.GetJsonAsync("/api/companies?q=CRUD Firma 2");
        Assert.Contains(list.Items(), c => c!["id"].Int() == id);

        // non-empty company cannot be deleted
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/companies/{fx.DemoCompanyId}"), HttpStatusCode.Conflict, "COMPANY_NOT_EMPTY");

        using var del = await admin.DeleteAsync($"/api/companies/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await ApiSession.ExpectProblemAsync(await admin.GetAsync($"/api/companies/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/companies/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");

        // soft-deleted: tax number reusable
        using var reuse = await admin.PostAsync("/api/companies", new { name = "Reuse", taxNumber = tax, isActive = true });
        Assert.Equal(HttpStatusCode.Created, reuse.StatusCode);
    }

    [Fact]
    public async Task Company_with_only_user_is_not_empty()
    {
        var admin = await fx.AdminAsync();
        var c = await admin.PostJsonAsync("/api/companies", new { name = "WithUser", taxNumber = TestData.TaxNumber(), isActive = true }, HttpStatusCode.Created);
        await admin.PostJsonAsync("/api/users", new { email = $"cu.{TestData.Code().ToLowerInvariant()}@test.local", fullName = "X Y", password = "Passw0rdX", role = "User", companyId = c["id"].Int(), isActive = true }, HttpStatusCode.Created);
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/companies/{c["id"].Int()}"), HttpStatusCode.Conflict, "COMPANY_NOT_EMPTY");
    }

    [Fact]
    public async Task User_crud_and_rules()
    {
        var admin = await fx.AdminAsync();
        var email = $"crud.{TestData.Code().ToLowerInvariant()}@test.local";

        // role rules
        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/users", new { email, fullName = "A B", password = "Passw0rdX", role = "User", isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/users", new { email, fullName = "A B", password = "Passw0rdX", role = "Admin", companyId = fx.DemoCompanyId, isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/users", new { email, fullName = "A B", password = "weak", role = "User", companyId = fx.DemoCompanyId, isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/users", new { email = "not-an-email", fullName = "A B", password = "Passw0rdX", role = "User", companyId = fx.DemoCompanyId, isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var u = await admin.PostJsonAsync("/api/users", new { email, fullName = "A B", password = "Passw0rdX", role = "User", companyId = fx.DemoCompanyId, isActive = true }, HttpStatusCode.Created);
        Assert.Equal("Demo Gıda A.Ş.", u["companyName"].Str());
        await ApiSession.ExpectProblemAsync(await admin.PostAsync("/api/users", new { email, fullName = "A B", password = "Passw0rdX", role = "User", companyId = fx.DemoCompanyId, isActive = true }), HttpStatusCode.Conflict, "DUPLICATE_EMAIL");

        // link to C001 which is already linked to ahmet → ACCOUNT_IN_USE
        var op = await fx.OperatorAsync();
        var c001 = (await op.GetJsonAsync("/api/current-accounts?q=C001")).Items().First()!;
        await ApiSession.ExpectProblemAsync(
            await admin.PutAsync($"/api/users/{u["id"].Int()}", new { fullName = "A B", role = "User", companyId = fx.DemoCompanyId, currentAccountId = c001["id"].Int(), isActive = true }),
            HttpStatusCode.Conflict, "ACCOUNT_IN_USE");

        // link to a fresh account → becomes portal user
        var admDemo = await fx.AdminDemoAsync();
        var acc = await admDemo.CreateAccountAsync("Customer");
        var upd = await admin.PutJsonAsync($"/api/users/{u["id"].Int()}", new { fullName = "A B C", role = "User", companyId = fx.DemoCompanyId, currentAccountId = acc["id"].Int(), isActive = true, newPassword = "Passw0rdY" });
        Assert.Equal(acc["id"].Int(), upd["currentAccountId"].Int());
        var portal = await fx.LoginAsync(email, "Passw0rdY");
        Assert.True(portal.User!["isPortalUser"]!.GetValue<bool>());
        await ApiSession.ExpectProblemAsync(await portal.GetAsync("/api/stocks"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // account linked to a user cannot be deleted
        await ApiSession.ExpectProblemAsync(await admDemo.DeleteAsync($"/api/current-accounts/{acc["id"].Int()}"), HttpStatusCode.Conflict, "ACCOUNT_IN_USE");

        var list = await admin.GetJsonAsync($"/api/users?q={email}");
        Assert.Single(list.Items());
        Assert.Equal(1, (await admin.GetJsonAsync($"/api/users?role=User&companyId={fx.DemoCompanyId}&q={email}"))["totalCount"].Int());

        using var del = await admin.DeleteAsync($"/api/users/{u["id"].Int()}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await ApiSession.ExpectProblemAsync(await admin.GetAsync($"/api/users/{u["id"].Int()}"), HttpStatusCode.NotFound, "NOT_FOUND");

        // cannot delete self
        var me = await admin.GetJsonAsync("/api/auth/me");
        await ApiSession.ExpectProblemAsync(await admin.DeleteAsync($"/api/users/{me["id"].Int()}"), HttpStatusCode.Conflict, "CANNOT_DELETE_SELF");
    }

    [Fact]
    public async Task Stock_crud_validation_and_normalization()
    {
        var op = await fx.OperatorAsync();
        var raw = "  " + TestData.Code("s").ToLowerInvariant() + " ";
        var created = await op.PostJsonAsync("/api/stocks", new { code = raw, name = "Normalize", unit = "Piece", vatRate = 20, unitPrice = 12.5m, isActive = true, balance = 999 }, HttpStatusCode.Created);
        Assert.Equal(raw.Trim().ToUpperInvariant(), created["code"].Str());
        Assert.Equal(0m, created["balance"].Dec()); // balance never accepted from input
        Assert.Equal(fx.DemoCompanyId, created["companyId"].Int());

        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/stocks", TestData.StockBody(raw.Trim())), HttpStatusCode.Conflict, "DUPLICATE_CODE");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/stocks", TestData.StockBody("BAD CODE!")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/stocks", new { code = TestData.Code(), name = "x", unit = "Ton", vatRate = 1, unitPrice = 1, isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED"); // BUG-2 fixed: strict problem+json

        var id = created["id"].Int();
        var got = await op.GetJsonAsync($"/api/stocks/{id}");
        var upd = await op.PutJsonAsync($"/api/stocks/{id}", new { code = created["code"].Str(), name = "Yeni Ad", unit = "Kg", vatRate = 10, unitPrice = 3, isActive = false, rowVersion = got["rowVersion"].Str() });
        Assert.Equal("Yeni Ad", upd["name"].Str());
        Assert.False(upd["isActive"]!.GetValue<bool>());
        Assert.NotEqual(got["rowVersion"].Str(), upd["rowVersion"].Str());
        await ApiSession.ExpectProblemAsync(await op.PutAsync($"/api/stocks/{id}", TestData.StockBody(created["code"].Str())), HttpStatusCode.BadRequest, "VALIDATION_FAILED"); // rowVersion required
        await ApiSession.ExpectProblemAsync(await op.GetAsync("/api/stocks/987654"), HttpStatusCode.NotFound, "NOT_FOUND");

        var admin = await fx.AdminDemoAsync();
        using var del = await admin.DeleteAsync($"/api/stocks/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await ApiSession.ExpectProblemAsync(await op.GetAsync($"/api/stocks/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.DoesNotContain((await op.GetJsonAsync($"/api/stocks?q={created["code"].Str()}")).Items(), s => s!["id"].Int() == id);

        // soft-deleted code reusable
        using var reuse = await op.PostAsync("/api/stocks", TestData.StockBody(created["code"].Str()));
        Assert.Equal(HttpStatusCode.Created, reuse.StatusCode);
        var stored = await fx.Factory.ScalarAsync<bool>($"SELECT IsDeleted FROM Stocks WHERE Id = {id}");
        Assert.True(stored); // soft delete, row still there
    }

    [Fact]
    public async Task Current_account_crud_and_validation()
    {
        var op = await fx.OperatorAsync();
        var code = TestData.Code("A");
        var created = await op.PostJsonAsync("/api/current-accounts", new { code, title = "Cari Test", type = "Customer", taxNumber = "1234567890", city = "Ankara", email = "x@y.com", isActive = true }, HttpStatusCode.Created);
        Assert.Equal("Customer", created["type"].Str());
        Assert.Equal(0m, created["balance"].Dec());
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/current-accounts", TestData.AccountBody(code)), HttpStatusCode.Conflict, "DUPLICATE_CODE");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/current-accounts", new { code = TestData.Code(), title = "", type = "Customer", taxNumber = "12a", isActive = true }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var id = created["id"].Int();
        var upd = await op.PutJsonAsync($"/api/current-accounts/{id}", new { code, title = "Cari Test 2", type = "Both", city = "Ankara", isActive = true, rowVersion = created["rowVersion"].Str() });
        Assert.Equal("Cari Test 2", upd["title"].Str());
        Assert.Equal("Both", upd["type"].Str());

        var filtered = await op.GetJsonAsync($"/api/current-accounts?type=Both&city=Ankara&q={code}");
        Assert.Single(filtered.Items());

        await ApiSession.ExpectProblemAsync(await op.DeleteAsync($"/api/current-accounts/{id}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        var admin = await fx.AdminDemoAsync();
        using var del = await admin.DeleteAsync($"/api/current-accounts/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await ApiSession.ExpectProblemAsync(await op.GetAsync($"/api/current-accounts/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Invoice_draft_crud_and_validation()
    {
        var op = await fx.OperatorAsync();
        var stock = await op.CreateStockAsync(vatRate: 20, unitPrice: 33.3333m);
        var stock2 = await op.CreateStockAsync();
        var acc = await op.CreateAccountAsync("Customer");

        // validation
        var p = await ApiSession.ExpectProblemAsync(
            await op.PostAsync("/api/invoices", new { type = "Sales", invoiceDate = TestData.Today, currentAccountId = acc["id"].Int(), lines = new[] { new { stockId = stock["id"].Int(), quantity = 0m, unitPrice = -1m, vatRate = 7m } } }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var keys = p["errors"]!.AsObject().Select(k => k.Key).ToList();
        Assert.Contains("lines[0].quantity", keys);
        Assert.Contains("lines[0].unitPrice", keys);
        Assert.Contains("lines[0].vatRate", keys);
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", new { type = "Sales", invoiceDate = TestData.Today, currentAccountId = acc["id"].Int(), lines = Array.Empty<object>() }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", new { type = "Sales", invoiceDate = "2026-05-10", dueDate = "2026-05-01", currentAccountId = acc["id"].Int(), lines = new[] { new { stockId = stock["id"].Int(), quantity = 1m, unitPrice = 1m, vatRate = 1m } } }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", new { type = "Sales", invoiceDate = TestData.Today, currentAccountId = acc["id"].Int(), lines = new[] { new { stockId = stock["id"].Int(), quantity = 1.2345m, unitPrice = 1m, vatRate = 1m } } }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", new { type = "Sales", invoiceDate = TestData.Today, currentAccountId = acc["id"].Int(), lines = Enumerable.Range(0, 201).Select(_ => new { stockId = stock["id"].Int(), quantity = 1m, unitPrice = 1m, vatRate = 1m }).ToArray() }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", TestData.InvoiceBody("Sales", 999999, [(stock["id"].Int(), 1, 1, 1)])), HttpStatusCode.UnprocessableEntity, "INVALID_REFERENCE");

        // create: 3 × 33.3333 @20% → net 100.00, vat 20.00, total 120.00 ; series default SAT
        var inv = await op.PostJsonAsync("/api/invoices", new
        {
            type = "Sales", invoiceDate = TestData.Today, currentAccountId = acc["id"].Int(), description = "Taslak test",
            subTotal = 1, grandTotal = 1, // totals from client must be ignored
            lines = new[] { new { stockId = stock["id"].Int(), quantity = 3m, unitPrice = 33.3333m, vatRate = 20m } },
        }, HttpStatusCode.Created);
        Assert.Equal("Draft", inv["status"].Str());
        Assert.Equal("SAT", inv["series"].Str());
        Assert.Null(inv["number"]?.GetValue<int?>());
        Assert.Null(inv["documentNo"]?.GetValue<string?>());
        Assert.Equal(100.00m, inv["subTotal"].Dec());
        Assert.Equal(20.00m, inv["vatTotal"].Dec());
        Assert.Equal(120.00m, inv["grandTotal"].Dec());
        Assert.Equal(1, inv["lines"]![0]!["lineNo"].Int());

        // update replaces all lines (2 lines now) and ignores type change
        var id = inv["id"].Int();
        var upd = await op.PutJsonAsync($"/api/invoices/{id}", new
        {
            type = "Purchase", series = "web", invoiceDate = TestData.Today, currentAccountId = acc["id"].Int(), rowVersion = inv["rowVersion"].Str(),
            lines = new[]
            {
                new { stockId = stock2["id"].Int(), quantity = 2m, unitPrice = 50m, vatRate = 1m },
                new { stockId = stock["id"].Int(), quantity = 1m, unitPrice = 0.005m, vatRate = 0m },
            },
        });
        Assert.Equal("Sales", upd["type"].Str());
        Assert.Equal("WEB", upd["series"].Str());
        Assert.Equal(2, upd["lines"]!.AsArray().Count);
        Assert.Equal(100.01m, upd["subTotal"].Dec()); // 0.005 → 0.01 (AwayFromZero)
        Assert.Equal(1.00m, upd["vatTotal"].Dec());
        Assert.Equal(101.01m, upd["grandTotal"].Dec());
        Assert.Equal(new[] { 1, 2 }, upd["lines"]!.AsArray().Select(l => l!["lineNo"].Int()).ToArray());

        await ApiSession.ExpectProblemAsync(await op.PutAsync($"/api/invoices/{id}", TestData.InvoiceBody("Sales", acc["id"].Int(), [(stock["id"].Int(), 1, 1, 1)], rowVersion: inv["rowVersion"].Str())), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");

        var list = await op.GetJsonAsync($"/api/invoices?status=Draft&currentAccountId={acc["id"].Int()}");
        Assert.Equal(2, list.Items().Single()!["lineCount"].Int());

        // delete draft (hard)
        using var del = await op.DeleteAsync($"/api/invoices/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        await ApiSession.ExpectProblemAsync(await op.GetAsync($"/api/invoices/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(0, await fx.Factory.ScalarAsync<int>($"SELECT COUNT(*) FROM InvoiceLines WHERE InvoiceId = {id}"));
    }

    [Fact]
    public async Task Invoice_with_inactive_stock_or_account_type_mismatch_rejected()
    {
        var op = await fx.OperatorAsync();
        var inactive = await op.CreateStockAsync(isActive: false);
        var customer = await op.CreateAccountAsync("Customer");
        var supplier = await op.CreateAccountAsync("Supplier");
        var stock = await op.CreateStockAsync();

        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", TestData.InvoiceBody("Sales", customer["id"].Int(), [(inactive["id"].Int(), 1, 1, 1)])), HttpStatusCode.UnprocessableEntity, "INACTIVE_STOCK");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", TestData.InvoiceBody("Purchase", customer["id"].Int(), [(stock["id"].Int(), 1, 1, 1)])), HttpStatusCode.UnprocessableEntity, "ACCOUNT_TYPE_MISMATCH");
        await ApiSession.ExpectProblemAsync(await op.PostAsync("/api/invoices", TestData.InvoiceBody("Sales", supplier["id"].Int(), [(stock["id"].Int(), 1, 1, 1)])), HttpStatusCode.UnprocessableEntity, "ACCOUNT_TYPE_MISMATCH");
    }
}
