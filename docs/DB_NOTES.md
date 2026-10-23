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
| D12 | Restore race with pooled API connections | `SINGLE_USER WITH ROLLBACK IMMEDIATE` alone lets a reconnecting API session take the single slot before RESTORE runs. restore.ps1 sets SINGLE_USER then OFFLINE (nobody can connect), restores, then MULTI_USER. |
| D13 | COPY_ONLY default | Ad-hoc, pre-deploy and scheduled backups are COPY_ONLY, so they never move a differential base or break a log chain someone else maintains. `-ChainBase` takes a normal full backup. |
| D14 | Turkish-I in scripts | PowerShell `-match`/`-like` are culture-sensitive. Under tr-TR, `[A-Za-z]` with IgnoreCase does not match `I`, which broke parsing of `DB_EXISTS=` etc. The scripts use only `-cmatch`/`-clike`/ordinal comparisons. Other agents' PowerShell should do the same. |
| D15 | Requests → Backend | (a) `ALTER DATABASE CURRENT SET AUTO_CLOSE OFF` (Express turns it ON for new DBs; local StockAccountDb is ON). (b) Docker `StockAccountDb` is in FULL recovery with no log backups, so the log only grows: set `RECOVERY SIMPLE` (RPO = backup interval) or add log backups. (c) Connection string values in §8.2. (d) `docker compose stop api` around restore in rollback.ps1, then start the previous image tag. Both (a) and (b) need `suppressTransaction: true` if done in a migration, or can be a one-time ops statement. **Status: applied in 50f7df1 (Backend)**: idempotent pre-listen init step in the db-migrate service (AUTO_CLOSE OFF, RECOVERY SIMPLE for docker), pool/Application Name values, least-privilege app login, rollback stops app services around the restore; drill verified, see BACKEND_NOTES §7.6. |
| D16 | BUG-9: cache after a standalone restore | restore.ps1 owns the cache flush (prefix `sa:` = the API's Redis InstanceName), so every restore path is safe, not only rollback.ps1. The flush is idempotent: rollback.ps1 flushing again afterwards just deletes 0 keys (it may also pass `-SkipCacheFlush`). Only the app prefix is touched; other tenants of the same Redis are unaffected. |

## 8. Production: backups, rollback, connection pooling

### 8.1 Backup / restore scripts (`scripts/db/`)
| Script | Purpose | Exit codes |
|---|---|---|
| `backup.ps1 -Target local\|docker [-Label pre-deploy-1.2.0] [-RetentionCount 14] [-ChainBase] [-LogFile f]` | Full backup, `COPY_ONLY` (unless `-ChainBase`), `CHECKSUM`, `COMPRESSION` when the edition supports it (EngineEdition 2/3; Express = 4 → uncompressed), `RESTORE VERIFYONLY WITH CHECKSUM`, SHA-256, JSON manifest, retention per label family. Returns the manifest object on the pipeline. | 0 ok, 1 failure |
| `restore.ps1 -Target local\|docker (-File <name> \| -Latest [-Label <label\|family>]) [-Force] [-NoSafetyBackup] [-SkipCacheFlush] [-CachePrefix sa:] [-RedisHost localhost -RedisPort 6379]` | SHA-256 check against the manifest, then confirmation (type the DB name; non-interactive callers must pass `-Force`). Takes a `pre-restore` safety backup, runs VERIFYONLY + FILELISTONLY, then SINGLE_USER WITH ROLLBACK IMMEDIATE → OFFLINE → `RESTORE WITH REPLACE, CHECKSUM, MOVE` → MULTI_USER. Afterwards it checks `DBCC CHECKDB WITH NO_INFOMSGS`, ONLINE/MULTI_USER, and that the migration head and per-table row counts equal the manifest. **Then it flushes the API cache (BUG-9):** every `sa:*` Redis key is removed with SCAN + UNLINK in batches of 500 (never FLUSHALL/FLUSHDB). docker: via `docker compose exec redis redis-cli`; local: `-RedisHost/-RedisPort` (default localhost:6379, e.g. sa-redis) through a built-in RESP client, password from `$env:REDIS_PASSWORD`. If Redis is not running or reachable, a WARNING says to restart the API (its in-memory fallback cache cannot be flushed from outside). `-Latest` skips `pre-restore` backups unless asked. | 0 ok, 1 failure, 2 not confirmed, **3 restored + verified but cache flush failed** (Redis reachable, returned an error) |
| `migration-head.ps1 -Target local\|docker [-Json]` | Prints the latest `__EFMigrationsHistory.MigrationId` (stdout only). | 0 found, 2 DB/table missing, 1 error |
| `pool-check.sql` | Read-only ops diagnostics: capacity, sessions per program/host/login/db, idle sessions with open transactions, blocked requests and head blockers, THREADPOOL/lock waits, DB options (AUTO_CLOSE, recovery, RCSI). Run commands are in its header. | – |
| `register-backup-task.ps1 -Target local\|docker [-At 02:00] [-RetentionCount 14] [-RunWhetherLoggedOn] [-RunNow] [-Unregister]` | Windows Task Scheduler daily job (`\StockAccount\StockAccountDb-Backup-<target>`), label `scheduled`, transcript in `backups\logs\`. Express has no SQL Agent. `-RunWhetherLoggedOn` (S4U) needs an elevated prompt. | 0/1 |
| `docker-compose.backup.yml` + `backup-loop.sh` | Optional compose sidecar for Linux/compose hosts: `docker compose -f docker-compose.yml -f scripts/db/docker-compose.backup.yml up -d`. Every `BACKUP_INTERVAL` (24h) it takes a COPY_ONLY + CHECKSUM + VERIFYONLY backup and writes the same manifest format; it keeps `BACKUP_RETENTION` (14) scheduled backups. | – |

- **Locations.** docker: `/var/opt/mssql/backup` in the container = `./backups` on the host (bind mount in docker-compose.yml). If the mount is missing, the scripts detect that with a marker-file probe and move files with `docker compose cp`, so they also work against an older running container. local: `<repo>\backups\local`. The SQL Server service account (`NT Service\MSSQL$SQLEXPRESS`) is granted Modify there automatically, because the server process writes the file, not the caller. `backups/` is git-ignored (`backups/.gitignore`).
- **Naming.** `<db>_<target>_<yyyyMMdd-HHmmss>Z_<label>.bak` + `.json`. The label must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`, e.g. `pre-deploy-1.2.0` (no `+build`). The label family (the label without its version suffix) is the retention group, so pre-deploy, scheduled, manual and pre-restore backups each keep their own last N.
- **Manifest** (`.json`): file, database, target, label, labelFamily, createdAtUtc, sizeBytes, sha256, checksumOk, hasBackupChecksums, copyOnly, compressed, migrationHead, migrationCount, recoveryModel, serverVersion/Edition, rowCounts (per table), host/server path.
- **Security.** The DB name, labels, file names and paths are regex-validated, then emitted with quote doubling (`N'…'`, `[…]`). There is no free-text SQL from arguments. The SA password comes from `$env:MSSQL_SA_PASSWORD` or `.env` and reaches sqlcmd only through the child's `SQLCMDPASSWORD`; it is never on a command line or in output. For docker, the SQL text travels base64-encoded in an env var (no stdin BOM or quoting problems on PowerShell 5.1). Restore accepts files only from the backup folder (path traversal is rejected) and only if the SHA-256 matches the manifest.
- **Versions.** Backups restore only into the same or a newer SQL Server major. Local SQLEXPRESS is 17.x (2025) and docker is 16.x (2022), so local → docker does not work; docker → local does.
- **Runs on** Windows PowerShell 5.1 and PowerShell 7. Local runner: `sqlcmd` if on PATH, else `Invoke-Sqlcmd` (SqlServer/SQLPS module). Docker CLI: `$env:DOCKER_EXE`, PATH, or the Docker Desktop install path.

**For deploy.ps1 / rollback.ps1 (Backend):**
```powershell
$headBefore = & "$root\scripts\db\migration-head.ps1" -Target docker              # exit 2 = fresh DB
$m = & "$root\scripts\db\backup.ps1" -Target docker -Label "pre-deploy-$Version"
if ($LASTEXITCODE -ne 0) { throw 'pre-deploy backup failed - deploy aborted' }      # $m.file, $m.migrationHead
# ... deploy; on failure:
docker compose stop api ui-vue ui-nuxt
& "$root\scripts\db\restore.ps1" -Target docker -File $m.file -Force               # or -Latest -Label "pre-deploy-$Version"
if ($LASTEXITCODE -ne 0) { throw 'DB restore failed' }
# start the previous image tag; check: migration-head.ps1 == $m.migrationHead
```

### 8.2 Schema rollback policy
- **Rollback = restore the pre-deploy backup.** No EF down-migrations are run (`Down()` exists but is not part of the process). A down-migration cannot bring back dropped data, and it has not been tested against real data. The restore gives back schema and data exactly as they were.
- Cost: writes made between the pre-deploy backup and the rollback are lost. The automatic `pre-restore` backup keeps them for manual recovery. Keep that window small: stop or drain the API before the pre-deploy backup if writes must not be lost, and decide on rollback quickly.
- Prefer **expand/contract migrations** (additive first, destructive in a later release). Then the previous app version still runs on the new schema, and many incidents can be handled by rolling back only the image, without a DB restore.
- Retention ≥ the number of releases you may need to roll back across (default 14 per family).

### 8.3 Connection pool settings (recommended)
SqlClient pools per connection string per process. One pooled connection is held per in-flight DB call (milliseconds for this app), so the pool size limits **concurrent DB calls**, not users.

| Keyword | SQL Server Express (local / single API) | Full SQL Server (Standard/Enterprise, docker Developer) | Why |
|---|---|---|---|
| `Max Pool Size` | **50** | **100** per API instance (default); keep `instances × Max Pool Size ≤ max_workers_count / 2` (8 cores → 640 workers → ≤ 320) | Express is capped at 4 cores / 1 socket and ~1.4 GB buffer pool, so more than about 50 parallel statements only queue inside SQL Server. A smaller pool fails fast in the app (pool-wait timeout) instead of starving server workers (THREADPOOL waits). |
| `Min Pool Size` | **5** | **10** | Warm connections after idle periods and restarts. With Express AUTO_CLOSE (turn it OFF, D15) it also avoids reopening the database. |
| `Connect Timeout` | **15** | **15** (30 across a WAN or for a failover listener) | Also the time a request waits for a free pooled connection. EF's `EnableRetryOnFailure` (5 retries, ≤ 10 s) handles transient failures. |
| `Command Timeout` | **30** | **30** | Same as `Database:CommandTimeoutSeconds`. Set it in one place only; EF's `CommandTimeout` wins when both exist. Use 300 s for `MigrateAsync` only (`Database.SetCommandTimeout`). |
| `Application Name` | `StockAccount.Api` | `StockAccount.Api` (+ `-worker` for separate processes) | Shows up as `program_name` in `pool-check.sql`. Without it EF Core 10 reports `EFCore/10.0.13 (...)`. |
| `ConnectRetryCount` / `ConnectRetryInterval` | 3 / 10 | 3 / 10 | Idle-connection resiliency after network blips. |
| `Pooling`, `MultipleActiveResultSets` | true, False | true, False | MARS is not needed and costs extra. |
| `Encrypt` / `TrustServerCertificate` | True / True (local dev) | **True / False with a real certificate** (compose internal network: True/True acceptable) | |
| `Load Balance Timeout` (Connection Lifetime) | 0 | 0 (300 behind an AG listener so connections rebalance) | |

Example (docker / full SQL Server):
`Server=mssql,1433;Database=StockAccountDb;User Id=stockaccount_app;Password=…;Encrypt=True;TrustServerCertificate=True;Application Name=StockAccount.Api;Max Pool Size=100;Min Pool Size=10;Connect Timeout=15;Command Timeout=30;ConnectRetryCount=3;ConnectRetryInterval=10;MultipleActiveResultSets=False`

Also:
- **Do not use `sa` in production.** Use an app login with `db_datareader`, `db_datawriter` and `EXECUTE`, and run migrations with a separate owner/ddladmin login (or a `MigrateOnStartup=false` deploy step). Backups need `db_backupoperator` (restore needs `dbcreator`/sysadmin), so the scripts keep using sa/Windows admin.
- No `AddDbContextPool`: the audit interceptor resolves the scoped `ICurrentUser`. DbContext pooling is not connection pooling, and connection pooling is on regardless.
- **Pool exhaustion** shows as `InvalidOperationException: Timeout expired ... obtaining a connection from the pool`. Run `pool-check.sql`: compare the session count per program with Max Pool Size, then check for sleeping sessions with `open_tran > 0` (leaked transaction) and for blocking chains.
- After a restore, pooled connections are dead. SqlClient drops them on first use and EF retries (drill: API Healthy and login 200 right after the restore), but restarting the API is cleaner.
- Express limits to plan for: max database size 10 GB on SQL Server 2022 Express and 50 GB on SQL Server 2025 Express (local is 17.x); compute is the lesser of 1 socket / 4 cores; ~1.4 GB buffer pool. Move to Standard well before the size cap.

### 8.4 Drill evidence (2026-10-22)
| Drill | Result |
|---|---|
| local SQLEXPRESS: backup `drill-1` → change ELMA name + delete 80 refresh tokens → `restore -Latest -Label drill` (non-interactive without `-Force` → exit 2 and nothing changed; with `-Force` → exit 0) | ELMA name and 80 tokens back; CHECKDB clean; 16 tables / 275 rows = manifest; pre-restore safety backup created |
| docker stack (./backups not yet mounted → `docker compose cp` fallback): backup `pre-deploy-0.0.1-drill` (compressed, Developer) → change ELMA → `restore -Latest -Label pre-deploy -Force` | restored; CHECKDB clean; head `20261021180606_InitialCreate` = manifest; API `/health` Healthy and login 200 without a restart |
| temporary container from the same image with the `./backups` bind mount: restore into an empty server (MOVE to instance defaults), backup on the mounted dir, `restore -Latest` | all exit 0, no copy step needed |
| compose sidecar (`docker-compose.backup.yml`, interval 20 s, keep 2) | 4 backups, retention removed the oldest 2; `restore -Latest -Label scheduled` from the sidecar manifest OK |
| Task Scheduler (`register-backup-task.ps1 -RunNow`, then `-Unregister`) | scheduled backup + transcript written |
| Negative tests | tampered .bak → SHA-256 mismatch, aborted before touching the DB; `-File ..\..\Windows\evil.bak` → rejected; DB name `x];DROP DATABASE y;--` → rejected by validation; retention keep 2 → oldest removed |
| BUG-9 re-drill (docker): backup `r9stale` → create stock (id 14), GET by id + list until `X-Cache: HIT` → restore | with `-SkipCacheFlush`: bug reproduced (id 14 still 200 HIT, list totalCount 1; id 14 was then handed out again to the next new stock, which is the id-reuse hazard). Default: "6 sa:* keys removed" → GET /stocks/14 **404** (not served from cache), list **X-Cache MISS, totalCount 0**. Flush unit checks: 1 212 keys removed with a foreign key `keepme:1` kept; RESP client 1 203 keys; zero matching keys → Flushed/0 (idempotent); unreachable port → WARNING; prefix `*` rejected |

### 8.5 Container precheck (BUG-12)

`Invoke-DbSql` / `Invoke-InContainer` (docker target) call `Assert-DbContainerReady` first: the mssql container must be `running` and `healthy` (waits up to 60 s while `starting`). A stopped/missing container now fails with an actionable message instead of `OCI runtime exec failed: cannot exec in a stopped container`. `deploy.ps1` starts a stopped mssql before the pre-deploy backup and aborts the deploy if the backup fails; `rollback.ps1` starts mssql/redis before `restore.ps1`.
