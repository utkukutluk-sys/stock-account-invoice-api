# DB Notes (owner: DB agent)

Companion to `docs/CONTRACT.md`. Describes what the persistence layer provides and what the Backend MUST do with it.

## 1. What exists

| Item | Location |
|---|---|
| Solution / build | `StockAccount.slnx`, `Directory.Build.props` (net10.0, nullable, implicit usings, `TreatWarningsAsErrors` for `src/**`), `Directory.Packages.props` (central package management, transitive pinning on) |
| Domain | `src/StockAccount.Domain/{Common,Enums,Entities}`: base types, enums, entities, `DomainException`, `DomainRules` (VAT rates, rounding, code normalization/regex, default series), `Invoice.RecalculateTotals/EnsureDraft/RenumberLines/FormatDocumentNo/DefaultSeriesFor`, `InvoiceLine.Calculate`, `CurrentAccount.IsCompatibleWith(InvoiceType)` |
| Identity types | `src/StockAccount.Application/Common/Identity/{AppUser,AppRole(+Roles),RefreshToken}.cs` |
| Abstractions | `Application/Common/Abstractions/{IApplicationDbContext,ICurrentUser,QueryFilterNames}.cs` |
| Paging/sorting | `Application/Common/Paging/{PagedResult,SortMap,QueryableSortingExtensions,InvalidSortFieldException}.cs` |
| DbContext | `Infrastructure/Persistence/AppDbContext.cs` (`IdentityDbContext<AppUser, AppRole, int>`), configurations in `Persistence/Configurations` |
| Interceptor | `Persistence/Interceptors/AuditSaveChangesInterceptor.cs` |
| DI | `services.AddPersistence(configuration)` in `Persistence/PersistenceServiceCollectionExtensions.cs` |
| Design-time | `Persistence/AppDbContextFactory.cs` (env `ConnectionStrings__Default`, else local SQLEXPRESS) |
| Migration | `Persistence/Migrations/*_InitialCreate.cs` + hand-written DDL in `InitialCreateSql.cs` (views + triggers) |
| Seed | `Persistence/Seed/DataSeeder.cs` → `DataSeeder.SeedAsync(IServiceProvider)` |

### Schema summary (verified on `.\SQLEXPRESS` / `StockAccountDb`)
- 16 tables (15 + `__EFMigrationsHistory`): Companies, Stocks, CurrentAccounts, Invoices, InvoiceLines, StockTrans, ActTrans, RefreshTokens, AspNetUsers/Roles/UserRoles/UserClaims/UserLogins/UserTokens/RoleClaims.
- 26 CHECK constraints, 21 FKs (only Identity children, RefreshTokens→Users and InvoiceLines→Invoices cascade; all business FKs `Restrict`), 8 filtered unique indexes.
- Database collation pinned to `Turkish_CI_AS` (`modelBuilder.UseCollation`) so local and Docker behave identically.
- Rowversion (`IsRowVersion`) on Companies, Stocks, CurrentAccounts, Invoices.
- Views `vw_StockBalanceReconciliation`, `vw_AccountBalanceReconciliation`; triggers `TR_StockTrans_Immutable`, `TR_ActTrans_Immutable`.

Key constraints worth knowing (DB will reject these even if app validation is bypassed):
| Constraint | Rule |
|---|---|
| `UX_Invoices_CompanyId_Series_Number` | unique (CompanyId, Series, Number) WHERE Number IS NOT NULL. Violation = SQL error **2601** |
| `CK_Invoices_Numbering` | Draft ⇔ Number and DocumentNo NULL; Approved/Cancelled ⇔ both set. **Set Status, Number, DocumentNo in the same SaveChanges.** |
| `CK_Invoices_Approval` | non-Draft requires ApprovedAt |
| `CK_Invoices_Cancellation` | Cancelled requires CancelledAt and CancelReason |
| `CK_Invoices_Totals`, `CK_InvoiceLines_LineTotal` | GrandTotal = SubTotal + VatTotal; LineTotal = LineNet + VatAmount (always call `RecalculateTotals()`) |
| `CK_InvoiceLines_Quantity/UnitPrice/VatRate/LineNo` | qty > 0, price ≥ 0, VAT ∈ {0,1,10,20}, LineNo 1..200 |
| `CK_Stocks_VatRate/UnitPrice/Unit`, `CK_CurrentAccounts_Type/TaxNumber`, `CK_Companies_TaxNumber` | enum ranges, VAT set, 10–11 digit tax numbers |
| `CK_StockTrans_Quantity` (> 0), `CK_*Trans_Direction`, `CK_*Trans_Amount` (≥ 0) | ledger sanity |
| `UX_Stocks_CompanyId_Code`, `UX_CurrentAccounts_CompanyId_Code`, `UX_Companies_TaxNumber` | unique among non-deleted rows (map 2601/2627 → 409 `DUPLICATE_CODE` / `DUPLICATE_TAX_NUMBER`) |
| `EmailIndex` (unique), `UserNameIndex`, `UX_AspNetUsers_CurrentAccountId` (filtered) | unique email; an account links to ≤ 1 user (→ 409 `ACCOUNT_IN_USE`) |
| `TR_StockTrans_Immutable`, `TR_ActTrans_Immutable` | any UPDATE/DELETE on ledgers raises error 50001/50002 |

## 2. Backend MUST-DOs

### 2.1 Registration
```csharp
builder.Services.AddPersistence(builder.Configuration);                // DbContext + IApplicationDbContext + interceptor
builder.Services.AddScoped<ICurrentUser, CurrentUser>();               // your HttpContext implementation
builder.Services.AddIdentityCore<AppUser>(...).AddRoles<AppRole>()
    .AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
```
The interceptor takes `ICurrentUser?` and `TimeProvider?` optionally (null user → audit user columns null). `src/StockAccount.Api/Program.cs` is a placeholder I wrote only to migrate + seed; replace it freely.

### 2.2 Execution strategy + transactions (IMPORTANT)
`AddPersistence` enables `EnableRetryOnFailure`. With a retrying strategy, a user-initiated `BeginTransactionAsync` **throws** unless it is wrapped in the execution strategy:
```csharp
var strategy = db.Database.CreateExecutionStrategy();
return await strategy.ExecuteAsync(async () =>
{
    await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
    ... // load, check, mutate
    await db.SaveChangesAsync(ct);   // ONE SaveChanges for ledger rows + balances + invoice status/number
    await tx.CommitAsync(ct);
    return result;
});
```

### 2.3 Approve / Cancel algorithm (CONTRACT §3.6)
Outer loop `for attempt in 1..3` (around the execution-strategy call above):
1. `db.ChangeTracker.Clear()` at the start of each attempt.
2. Load the invoice with lines (tracked, tenant-scoped). Attempt 1 only: compare the client `rowVersion` to `invoice.RowVersion` (`SequenceEqual`), mismatch → 409 `CONCURRENCY_CONFLICT`. Also set `db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = invoice.RowVersion` (already true when freshly loaded).
3. Re-check status (`EnsureDraft` / Approved), lines, active account/stocks, type compatibility, negative stock (`Stock.Balance - Σ qty < 0`).
4. Approve: `Number = (await db.Invoices.Where(i => i.CompanyId == c && i.Series == s).MaxAsync(i => (int?)i.Number) ?? 0) + 1`; `DocumentNo = Invoice.FormatDocumentNo(series, number)`. The MAX uses `UX_Invoices_CompanyId_Series_Number` (seek).
5. Add StockTrans per line and one ActTrans; update `Stock.Balance` / `CurrentAccount.Balance` on the **tracked** entities (their RowVersion guards concurrent balance updates); set Status/ApprovedAt/ApprovedByUserId (or Cancelled* fields). Round: balances are sums of already-rounded values; quantities have 3 decimals.
6. `SaveChangesAsync` once → `CommitAsync`.
7. Catch `DbUpdateConcurrencyException` **or** `DbUpdateException` whose inner `SqlException.Number is 2601 or 2627` (index name `UX_Invoices_CompanyId_Series_Number`) → rollback (dispose tx), retry. After 3 failures → 409 `CONCURRENCY_CONFLICT`.
8. Invalidate cache only after commit.

Why not UPDLOCK/SERIALIZABLE: rowversion on Stock/CurrentAccount/Invoice + the unique numbering index already make every race end in a detectable, retryable failure, without holding range locks. Do **not** raise the isolation level; do not use raw `UPDLOCK` hints (they would need raw SQL).

### 2.4 Optimistic concurrency on PUT
Set the client rowVersion as original value before saving:
`db.Entry(entity).Property(e => e.RowVersion).OriginalValue = Convert.FromBase64String(request.RowVersion);` → `DbUpdateConcurrencyException` → 409 `CONCURRENCY_CONFLICT`.

### 2.5 Soft delete & query filters
- `db.Stocks.Remove(stock)` is converted by the interceptor into `IsDeleted = true, DeletedAt, DeletedByUserId` (+ UpdatedAt). Do not load/track dependents before removing (Restrict FKs).
- Filters are **named** (EF Core 10): `QueryFilterNames.SoftDelete`. Bypass with `.IgnoreQueryFilters([QueryFilterNames.SoftDelete])`.
- **History queries:** InvoiceLine→Stock, Invoice→CurrentAccount, StockTrans→Stock, ActTrans→CurrentAccount are *required* navigations to soft-deletable entities. EF turns a filtered required navigation into an INNER JOIN, so **a line/invoice/ledger row whose stock/account was soft-deleted would silently disappear**. For invoice detail/list, ledger lists, dashboard recent invoices and portal queries, start from the non-soft-deletable root and use `.IgnoreQueryFilters([QueryFilterNames.SoftDelete])` (tenant scoping is explicit anyway). The EF startup warning for this is suppressed on purpose.
- Use the default (filtered) sets for Stocks/CurrentAccounts/Companies CRUD and lookups.

### 2.6 Tenant scoping
CONTRACT §3.8 requires **explicit** `ForTenant(ICurrentUser)` in Application (no global tenant filter). Every tenant entity implements `ITenantEntity`, so one generic extension works:
`public static IQueryable<T> ForTenant<T>(this IQueryable<T> q, ICurrentUser u) where T : class, ITenantEntity => u.CompanyId is int c ? q.Where(e => e.CompanyId == c) : q;` (non-admins always have CompanyId).

### 2.7 Ledger immutability
Never update/delete StockTrans/ActTrans: the interceptor throws `InvalidOperationException` and the DB triggers raise errors 50001/50002. Cancellation writes reversal rows.

## 3. Sorting / paging helper (injection-safe)

```csharp
// one static whitelist per list endpoint (field names = API names from CONTRACT §5)
static readonly SortMap<Stock> StockSorts = new SortMap<Stock>("code", s => s.Id)
    .Add("code", s => s.Code).Add("name", s => s.Name).Add("unit", s => s.Unit)
    .Add("unitPrice", s => s.UnitPrice).Add("balance", s => s.Balance).Add("createdAt", s => s.CreatedAt);

// validator:   RuleFor(x => x.Sort).Must(s => StockSorts.IsValid(s, out _)).WithMessage("Geçersiz sıralama alanı.");
// service:
var page = await db.Stocks.ForTenant(user)
    .Where(...filters...)
    .ApplySort(request.Sort, StockSorts)      // unknown field → InvalidSortFieldException (map to 400 VALIDATION_FAILED, errors["sort"])
    .Select(s => new StockDto(...))           // projection after sort
    .ToPagedResultAsync(request.Page, request.PageSize, ct);
```
- Sort strings are only dictionary keys → typed expression trees; nothing from the client becomes SQL text. Id asc tie-breaker is appended automatically unless `id` is already in the sort (e.g. `-transDate,-id`).
- Free-text `q`: use LINQ `x.Name.Contains(q)` — EF parameterizes and escapes `%`, `_`, `[` (verified: `LIKE ... ESCAPE N'\'`). Case-insensitivity comes from the `Turkish_CI_AS` collation; do **not** call `ToLower()` (kills index use, and Turkish `I/ı` rules differ).
- Codes are stored `ToUpperInvariant`. Under Turkish collation, `i` ≠ `I` (Turkish `i` ↔ `İ`); when `q` should match codes typed in lowercase ASCII, also compare against `DomainRules.NormalizeCode(q)`.
- `PagedResult<T>` (`items,page,pageSize,totalCount,totalPages`) matches CONTRACT §5.1. Backend may add `PagedRequest` etc. next to it.

**No raw SQL anywhere in the DB layer** (only static DDL in the migration). If raw SQL is ever needed, use `FromSql($"... {param}")` / `ExecuteSql($"...")` (interpolated → parameters); never `FromSqlRaw` with concatenation.

## 4. Balance reconciliation
Views (all companies, incl. soft-deleted; `Difference = StoredBalance − LedgerBalance`) mapped as keyless sets on `AppDbContext` (not on `IApplicationDbContext`):
```csharp
var stockIssues   = await db.StockBalanceChecks.Where(x => x.Difference != 0).ToListAsync(ct);
var accountIssues = await db.AccountBalanceChecks.Where(x => x.Difference != 0).ToListAsync(ct);
var stocksChecked = await db.StockBalanceChecks.CountAsync(ct);
```
AutoFix: load the entity with `IgnoreQueryFilters([QueryFilterNames.SoftDelete])`, set `Balance = LedgerBalance`, SaveChanges (rowversion protects against a concurrent approve; on conflict just log and let the next run re-check).
Pure-EF equivalent (if views are not wanted): `db.Stocks.IgnoreQueryFilters().Select(s => new { s.Id, s.Balance, Ledger = db.StockTrans.Where(t => t.StockId == s.Id).Sum(t => t.Direction == StockTransDirection.In ? t.Quantity : -t.Quantity) })`.
Covering indexes `IX_StockTrans_StockId_TransDate INCLUDE (Direction, Quantity)` and `IX_ActTrans_CurrentAccountId_TransDate INCLUDE (Direction, Amount)` serve these aggregations.
Integration test 13 ("tamper Stocks.Balance via SQL") works: only the ledgers are trigger-protected.

## 5. Seed (CONTRACT §9)
`await DataSeeder.SeedAsync(app.Services)` creates its own scope. Idempotent (looked up by role name, tax number, codes, email; invoice only if the company has none). Verified: second run inserts nothing.
- Users are created via `UserManager<AppUser>` → **Identity must be registered before seeding**, otherwise users are skipped with a warning.
- The opening purchase invoice is created as **Draft** (ALS, S001, 5 lines, totals 17,400.00 / 1,314.00 / 18,714.00). **TODO Backend (BE-9):** after seeding, approve every Draft invoice with `Description == DomainRules.SeedOpeningInvoiceDescription` via `IInvoiceService.ApproveAsync` (system context, no user) → ELMA balance 100, S001 balance −18,714.00.

## 6. Migrations
- Tool: local manifest `dotnet-tools.json` (dotnet-ef 10.0.13) → `dotnet tool restore`, then `dotnet ef ...` (no global install needed).
- Add: `dotnet ef migrations add <Name> -p src/StockAccount.Infrastructure -s src/StockAccount.Api -o Persistence/Migrations`
- Apply locally: `dotnet ef database update -p src/StockAccount.Infrastructure -s src/StockAccount.Api`
- **Docker / CI idempotent script:** `dotnet ef migrations script --idempotent -p src/StockAccount.Infrastructure -s src/StockAccount.Api -o artifacts-sql/migrate.sql` (folder git-ignored); run with `sqlcmd -I -C -S localhost,14333 -U sa -P ... -d StockAccountDb -i migrate.sql`. The script assumes the database exists: create it with `CREATE DATABASE StockAccountDb COLLATE Turkish_CI_AS`. Note `-I` (QUOTED_IDENTIFIER ON) is required because of filtered indexes. In compose the API's `MigrateAsync()` creates the DB itself (with the collation), so the script is optional.
- Design-time factory reads `ConnectionStrings__Default` env (e.g. to target the Docker SQL on port 14333).

## 7. Decisions / Requests
| # | Topic | Decision |
|---|---|---|
| D1 | Brief asked for a global tenant filter via `ICurrentTenant` | CONTRACT §3.8 says explicit `ForTenant(ICurrentUser)`, no global tenant filter. **Followed the contract**; `ITenantEntity` enables a generic `ForTenant` (§2.6). |
| D2 | Brief said "sln" | CONTRACT/PLAN say `StockAccount.slnx` → created `.slnx`. |
| D3 | Brief said users belong to Backend | CONTRACT §9/PLAN DB-6 assign users to DataSeeder via `UserManager` → implemented; only the approval of the opening invoice is left to Backend. |
| D4 | Where AppUser/AppRole/RefreshToken live | Domain must have no package deps, but `IApplicationDbContext` must expose `Users`/`RefreshTokens`. Placed in `Application/Common/Identity` (Application references `Microsoft.Extensions.Identity.Stores`). |
| D5 | `ActTrans.Amount` "always > 0" | A Draft with all unit prices 0 is valid (unitPrice ≥ 0) and would produce GrandTotal 0. DB check is `Amount >= 0` so approval cannot crash. **REQUEST → PM/Backend:** decide whether to skip the ActTrans row when GrandTotal = 0 or reject approval. Same for StockTrans.Amount (≥ 0); StockTrans.Quantity stays > 0. |
| D6 | Soft-delete filter vs required navigations | Named filter + guidance to use `IgnoreQueryFilters([SoftDelete])` for history queries (§2.5). |
| D7 | Extra integrity beyond contract | Numbering/approval/cancellation/total CHECKs, ledger immutability triggers, unique email index, collation pin. All consistent with CONTRACT rules. |
| D8 | dotnet-ef | Local tool manifest instead of global install (reproducible); global install still works. |
| D9 | Vulnerable transitive package | `Microsoft.Data.SqlClient 6.1.7` pulls `System.Security.Cryptography.Pkcs 9.0.11` (GHSA-2fmv-rv2c-59x7, GHSA-846f-f764-76mx) → pinned 10.0.13 via central transitive pinning (build is warnings-as-errors). |
| D10 | Extra indexes | `IX_Companies_Name`, `IX_Invoices_CompanyId_CreatedAt` (dashboard recent), `IX_*Trans_CompanyId_TransDate` (global ledger lists), `IX_InvoiceLines_StockId`, `IX_AspNetUsers_CompanyId`. |
| D11 | Api/launchSettings | Minimal `Program.cs` (migrate + seed + placeholder Identity) and launchSettings on http://localhost:5080 created so the solution builds; Backend owns them now. Test projects are empty shells (template versions of xunit 2.9.3 / runner 3.1.4 / Test.Sdk 17.14.1). |
