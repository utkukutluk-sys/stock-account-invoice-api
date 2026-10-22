# Test Report (owner: Test agent)

Date: 2026-10-22 · Contract: `docs/CONTRACT.md` v1.1 · Environment: Windows 11, .NET SDK 10.0.402, Node 24.22, SQL Server Express `.\SQLEXPRESS`, Docker 29.9.2, Playwright 1.64.1 (Chromium), newman 6.

## 0. Round 3 — Production readiness (independent verification, git e0ae9c8, shared stack 1.4.0)

This round re-checks the claims of commits 706a2b7…e0ae9c8 (BACKEND_NOTES §7, DB_NOTES §8/D15, FRONTEND_NOTES "Production readiness") with my own scripts in `tests/prod-readiness/`, not the authors' tests. All of them are re-runnable:

| Script | Purpose |
|---|---|
| `rate-limit-coverage.mjs` | Every swagger operation against a Development API with tiny limits |
| `proxy-ip-check.sh` | Client IPs through ui-vue nginx, the ui-nuxt proxy and the API port |
| `env-validation.sh` | Fail-fast config, both images, `compose config` |
| `fuzz-errors.mjs` / `fuzz-ui-errors.mjs` | Leak scan, 55 API cases and 42 UI cases |
| `pool-health-check.sh` + `parallel-load.mjs` | Pool and live/ready health |

The drill commands are in §0.5. The stack was left on **1.4.0, all 5 services healthy**, with `releases.json` `current = 1.4.0` and the Redis `sa:*` cache flushed.

### 0.1 Checklist result

| # | Item | Result | Evidence |
|---|---|---|---|
| 1a | Rate limiting covers **all** endpoints | **PASS** | All **43/43** swagger operations return 429 on request N+1 (global 5/3 s per user, auth 2/3 s per IP for login/refresh), with `application/problem+json`, `code RATE_LIMITED` and `Retry-After`. Per-user partition: admin blocked while operator on the same IP is served. Per-IP partition for anonymous callers. Unknown `/api` routes are limited too. The auth policy is stricter and separate from the global bucket. `/health`, `/health/live`, `/health/ready`: 30/30 requests → 200. Waiting Retry-After → served again. |
| 1b | Through ui-vue nginx / ui-nuxt proxy: separate buckets per client IP, spoofed XFF does not bypass | **PASS** | Each UI separately: host 100×401 then 429; host with a spoofed `X-Forwarded-For` (single value or chain) → still 429; a second client (container on the compose network) → 401, its own bucket. |
| 1c | Spoofed XFF directly against the published API port | **FAIL → BUG-7** | `localhost:5080`: after 100×401 + 429, every request with a new spoofed XFF gets **401** again. 101 attempts with 101 fake IPs → 101 × 401, so login brute force is unlimited. |
| 2 | Fail-fast startup validation | **PASS (1 gap → BUG-11)** | 17/18 cases. API image in Production exits **2** with one clear FTL line for: Jwt:Key missing / placeholder (change-me, DEV-ONLY image default) / too short; ConnectionStrings:Default empty; runtime `User Id=sa`; Redis enabled without a connection string; `0.0.0.0/0`; `::/0`; CORS `*`; and several errors reported at once. ui-nuxt image exits **1** when `NUXT_API_BASE` is missing, not absolute, or ends with `/api`. `docker compose config` exits 1 without JWT_KEY, MSSQL_SA_PASSWORD or APP_DB_PASSWORD. Distinctive secret values (passwords and keys) never appear in any output, and no secret appears in the logs of the api, db-migrate or Production containers. Positive control: a valid Production config starts and is ready. Gap: `0.0.0.0/1` + `128.0.0.0/1`, which trusts everyone, is accepted. |
| 3 | Error handling hardening (Production), no leaks | **PASS (2 low findings: BUG-8, BUG-10)** | API, 55 fuzz cases (malformed or deep JSON, decimal overflow, 5 MB / 40 MB bodies, wrong content types, invalid route values, NUL, traversal, 100 KB URL, 64 KB header, bad X-Company-Id / bearer / alg none, 405, TRACE, OPTIONS, unknown routes, Swagger and appsettings/.env probes): **0 leaks**. No stack, type names, paths, SQL or `Server` / `X-Powered-By` header in any response. Swagger is off. 413 / 405 are problem+json. Findings: `page=2147483647` → **500** (BUG-8, generic body, no leak), and 415 is `application/json` instead of problem+json (BUG-10). UIs, 42 probes: **0 real leaks**. The scanner flagged 8 false positives on hashed `/_nuxt/9N0…js` names. Nuxt 404/500 pages are Turkish without stack; nginx default 400/405/413/414 pages show "nginx" without a version. |
| 4 | DB pool + health | **PASS** | Effective runtime string: `User Id=stockaccount_app; Application Name=StockAccount.Api; Max Pool Size=100; Min Pool Size=10; Connect Timeout=15; Encrypt=True`. 2 × 400 parallel DB-bound requests: **800/800 × 200**, p95 ≈ 0.6 s, peak **38** `StockAccount.Api` sessions (≤ 100), **0** pool-timeout log lines. SQL stopped: `/health/live` 200; `/health/ready` **503 Unhealthy** (after 15.7 s = Connect Timeout); ready again 16 s after the SQL start. Redis stopped: ready **200 Degraded**, requests 200 (in-memory fallback), back to Healthy after the start. Docker health: api uses `/health/live` and stays `healthy` while SQL is down (by design); the UIs stay healthy. |
| 5 | Rollback + backups | **PASS (1 finding → BUG-9)** | See §0.5. The one-command claim holds: `rollback.ps1` with no arguments. |
| R | Full regression | **PASS** | See §0.2. |

### 0.2 Regression (round 3)

| Suite | Result |
|---|---|
| `dotnet build` | 0 warnings / 0 errors |
| Unit | **99/99** |
| Integration | **162/162**, 0 skipped. The Redis outage test now takes ~4 s instead of 54 s, which confirms the BUG-6 fix. |
| newman (Docker stack) | **115 requests / 350 assertions / 0 failed** |
| Playwright vue + nuxt + vue-mobile + nuxt-mobile | **58 tests: 56 passed, 2 skipped** (Nuxt-only on vue), 0 failed. Dev servers were run on 5174/3001 (new `E2E_VUE_PORT` / `E2E_NUXT_PORT`) next to the Docker stack. |
| `npm run check:hydration` (ui-nuxt, against the container) | OK in 2 time zones |
| Independent hydration test (`e2e/tests/hydration.spec.ts`, Nuxt container, America/Los_Angeles, 13 pages × light/dark) | **PASS**, no hydration warnings |

### 0.3 New bugs

| ID | Severity | Owner | Title | Status |
|---|---|---|---|---|
| BUG-7 | **High** | Backend (Docker config / compose) | Rate-limit bypass with a spoofed `X-Forwarded-For` on the published API port | Open |
| BUG-8 | Low | Backend | `page=2147483647` → 500 (int overflow → negative OFFSET) | Open |
| BUG-9 | Medium | DB (`scripts/db/restore.ps1`) | A standalone restore leaves the Redis cache stale, so deleted rows are still served | Open |
| BUG-10 | Low | Backend | 415 responses use `application/json` instead of `application/problem+json` | Open |
| BUG-11 | Low | Backend | The ForwardedHeaders check only rejects the literal `0.0.0.0/0` / `::/0` | Open |

**BUG-7 — Rate-limit bypass via spoofed X-Forwarded-For on :5080 (High, Backend)**

Repro (`tests/prod-readiness/proxy-ip-check.sh`, last phase, or by hand):
1. Send 101 failed logins to `http://localhost:5080/api/auth/login`. The result is 100×401 and 1×429.
2. Send the same request with the header `X-Forwarded-For: 203.0.113.50`. It returns **401**, not 429.
3. Send 101 more requests, each with a new fake IP. All of them return **401**.

- Expected: the client cannot choose its rate-limit partition. Login brute-force protection (`auth` policy, per IP) must hold.
- Cause:
  - `appsettings.Docker.json` trusts `172.16.0.0/12`, `192.168.0.0/16` and `10.0.0.0/8` as proxies.
  - Host traffic to the published port arrives from the Docker gateway, which is inside those ranges, so the API believes any XFF the caller sends.
  - Through the UI proxies the limit holds: nginx and Nitro append the real peer and `ForwardLimit=1`.
- Fix options:
  - Trust only the UI containers (fixed IPs / a dedicated proxy subnet without the gateway).
  - Or stop publishing 5080 outside localhost / dev.
  - Production must list its real proxies; the validator does not enforce that.

**BUG-8 — Paging overflow gives 500 (Low, Backend)**
- Repro: `GET /api/stocks?page=2147483647&pageSize=100`. The same happens on `/api/invoices`, `/api/stock-trans` and `/api/users`.
- Result: 500 `INTERNAL_ERROR`. The body is generic, with no leak.
- Log: `SqlException: The offset specified in a OFFSET clause may not be negative`.
- Expected: 400 `VALIDATION_FAILED`, either by capping `page` so that `(page-1)*pageSize` fits in an int, or by computing the skip in `long`.

**BUG-9 — Standalone `restore.ps1` does not flush the API cache (Medium, DB; Backend for the docs)**

Repro:
1. Run `backup.ps1 -Target docker -Label r3stale`.
2. Create stock DRILLD (id 12) and read it twice, so the second read is `X-Cache: HIT`.
3. Run `restore.ps1 -Target docker -Latest -Label r3stale -Force`. It ends with RESTORE OK.
4. Read DRILLD again by id and through the list.

- Result: `GET /api/stocks/12` → **200 X-Cache HIT** and the list shows DRILLD with `totalCount=1`, although the row no longer exists in the database (`COUNT(*)=0`).
- This lasts for up to the cache TTL (10 min by id, 5 min for lists). Because the identity value is also rolled back, the next new stock can get id 12 again, and the stale DTO would then be served for a different row.
- Only `rollback.ps1` flushes `sa:*`.
- Expected: `restore.ps1 -Target docker` flushes `sa:*` itself, or refuses to finish without doing so. Restore documentation should mention it.
- I flushed the keys by hand after the drill.

**BUG-10 — 415 is not problem+json (Low, Backend)**
- Repro: `POST /api/stocks` with `Content-Type` set to `text/plain`, `application/xml` or `application/x-www-form-urlencoded`, or with no content type.
- Result: 415, with a ProblemDetails body (`code VALIDATION_FAILED`) but **`Content-Type: application/json`**. `/api/auth/refresh` returns problem+json correctly.

**BUG-11 — Trust-all forwarded networks bypass the strict check (Low, Backend)**
- Repro: run the API image with `ASPNETCORE_ENVIRONMENT=Production`, `ForwardedHeaders__KnownNetworks__0=0.0.0.0/1` and `ForwardedHeaders__KnownNetworks__1=128.0.0.0/1`.
- Result: the API starts ("Now listening").
- Expected: rejected like `0.0.0.0/0`, for example when a prefix is ≤ /8 or not in private/loopback space, or when the networks cover the whole address space.

### 0.4 Observations (not filed as bugs)
- 405 responses carry `code: NOT_FOUND`; `METHOD_NOT_ALLOWED` would be clearer.
- `/health/ready` needs about 15.7 s to report Unhealthy when SQL is down, because of Connect Timeout 15. Probe timeouts must be longer than that (k8s default is 1 s).
- After a SQL restart only 1 pooled session remained until load arrived, so Min Pool 10 is not re-established immediately. This is harmless.
- The pool cap of 100 was never reached. 800 requests peaked at 38 sessions, so the "≤ Max Pool" property was observed but not stressed to the cap.
- ui-vue nginx limits bodies to 1 MB. A 1–30 MB JSON body through `/api` gets nginx's **HTML** 413, while the API itself answers problem+json. Real requests are far below 1 MB.
- nginx default error pages (400/405/414) carry no CSP and say "nginx" without a version.
- Nuxt 404 status lines echo the path ("404 Page Not Found: /invoices/abc"). It is harmless.
- The Docker API logs `No XML encryptor configured`: Data Protection keys are kept only in the container. This is unused by JWT/cookies here, but worth noting.
- I could not make a deploy fail through a genuinely broken build, because temporarily editing `src/` was blocked as a shared-code change. The failing deploy therefore used a readiness budget that was too short (`-ReadyTimeoutSeconds 2`). This drives the real failure → auto-rollback → DB-restore path, but not a crashing container.

### 0.5 Rollback / backup drill (shared stack, 2026-10-22 13:25–13:31 UTC)

| Step | Command | Result | Time |
|---|---|---|---|
| D1 deploy | `deploy.ps1 -Version 1.4.1-drill` | pre-deploy backup OK, images tagged `:1.4.1-drill` + `:sha-e0ae9c823c5d`, api `APP_VERSION=1.4.1-drill`, healthy | **17 s** |
| write | `POST /api/stocks` DRILLA | 201, list HIT | – |
| D2 rollback, **one command** | `rollback.ps1` (no arguments) | 1.4.1-drill → 1.4.0, all `:1.4.0` and healthy; DRILLA **kept** (no DB restore) | **9 s** |
| D3 deploy | `deploy.ps1 -Version 1.4.2-drill` | DEPLOYED | **14 s** |
| write | DRILLB (id 11), list and by-id reads up to `X-Cache HIT` | – | – |
| D3 rollback + DB | `rollback.ps1 -RestoreDatabase` | app stopped; restore from `pre-deploy-1.4.2-drill` (VERIFYONLY, CHECKDB clean, 16 tables / 231 rows = manifest); `sa:*` flushed; 1.4.0 healthy. DRILLB **gone** (list 0, by id 404, `X-Cache MISS`); DRILLA **kept**; runtime sessions `stockaccount_app` × 10; DB `MULTI_USER`, `ONLINE`, `AUTO_CLOSE 0`, `SIMPLE` | **13 s** |
| D4 failing deploy | `deploy.ps1 -Version 1.4.3-drill -ReadyTimeoutSeconds 2` | DEPLOY FAILED → **automatic** rollback to 1.4.0 with DB restore from `pre-deploy-1.4.3-drill`; exit 1; DRILLC (written before the deploy) kept; `current = 1.4.0` | **25 s** (rollback 12 s) |
| D4b | `rollback.ps1 -Version 9.9.9` | "image … not found locally", exit 1, stack untouched | – |
| D5 retention | `backup.ps1 -Label r3drill -RetentionCount 2` × 3 | each exit 0 (1–2 s), only the newest 2 kept | – |
| D5 corrupt | 16 bytes flipped in the newest `.bak` → `restore.ps1 -Latest -Label r3drill` (with or without `-Force`) | **refused**: "SHA-256 mismatch … (file changed or corrupted)", exit 1; nothing touched (no pre-restore backup, DB online) | 0 s |
| D5 confirm | `restore.ps1 -File <intact>` without `-Force` | "Refusing to restore without -Force in non-interactive mode", exit 2 | – |
| D5 restore | same with `-Force`; then a new backup + `restore.ps1 -Latest -Label r3drill -Force` | both RESTORE OK, CHECKDB clean, row counts = manifest | 7 s / 3 s |
| D5 stale cache | standalone restore after caching a new row | **BUG-9** | – |

## 1. Summary

### 1.1 Round 2: regression after the fixes (d918312 Backend BUG-1/2/3/5, f5be41d Frontend BUG-4), from a clean state

| Suite | Result | Duration |
|---|---|---|
| `dotnet build StockAccount.slnx` | 0 warnings, 0 errors | – |
| Unit | **90 / 90 passed** | ~1 s |
| Integration | **153 / 153 passed, 0 skipped**. New: Swagger regression (44), time-based rules (4), Redis runtime outage (1) | ~62 s (54 s of it is the Redis outage test, see BUG-6) |
| newman, local API (the "Regression – fixed bugs" folder now always runs) | **115 requests, 350 assertions, 0 failed** | ~4 s |
| Swagger re-check | 43 operations, **all have a 2xx response**, Bearer only on `[Authorize]` operations (login/refresh/logout anonymous), `X-Company-Id` on operator endpoints | – |
| Playwright, local dev servers, 4 projects (vue, nuxt, vue-mobile, nuxt-mobile) | **58 tests: 56 passed, 2 skipped** (Nuxt-only tests on vue), 0 failed. The BUG-4 test is a normal passing test now, and the Nuxt logout workaround was removed | 4.5 min |
| Docker: `compose up -d --build` (api + ui-nuxt rebuilt) | all 5 services healthy; newman **115 / 350 / 0 failed**; Playwright `@smoke` **17 passed, 1 skipped** (incl. both mobile projects); BUG-4, dark-mode and proactive-refresh tests **6/6** on the production images; `down -v`, sa-redis restarted | 36 s up, 1.5 min smoke |

Bug status: BUG-1…BUG-6 all **Verified** (BUG-6 found in round 2, fixed in 9b9c963 and verified by the tightened `RedisOutageTests`; §5).

### 1.2 Round 1: initial run (history)

| Suite | Location | Result | Duration |
|---|---|---|---|
| Unit (written by Backend, B9; verified) | `tests/StockAccount.UnitTests` | **90 / 90 passed** | ~1 s |
| Integration (WebApplicationFactory + SQLEXPRESS) | `tests/StockAccount.IntegrationTests` | **102 passed, 2 skipped** (open bugs BUG-1/BUG-2, see `KnownBugTests`), 0 failed; stable over 3 consecutive runs | ~8 s tests (~17 s wall incl. build + DB create/drop) |
| Postman / newman, local API | `postman/` | **113 requests, 341 assertions, 0 failed** (1 skipped assertion: BUG-1) | 3.9 s |
| Playwright E2E, ui-vue (vite dev) + ui-nuxt (nuxt dev) | `e2e/` | **42 tests: 40 passed** (incl. 1 *expected failure* for BUG-4 on Nuxt), **2 skipped** (Nuxt-only tests on the vue project), 0 unexpected failures | 4.1 min (both UIs, incl. server start) |
| Docker full stack (compose build + up) | `docker-compose.yml` | all 5 images built on first try; all services healthy; newman **113/341/0 failed**; Playwright smoke **15 passed** (incl. BUG-4 expected failure on Nuxt), 1 skipped | build 1 min 11 s, smoke 1.9 min |
| Swagger check | `/swagger/v1/swagger.json` | all 42 contract `/api` operations documented, Bearer scheme present; documentation gaps → BUG-3 | – |

Bugs found: **6** in total. Round 1: 5 (1 High Frontend, 1 Medium Backend, 3 Low Backend), all Verified fixed in round 2. Round 2: 1 new (BUG-6, Medium, Backend). Details in §5.

## 2. How to run

Prerequisites: SQLEXPRESS reachable with Windows auth, `sqlcmd` on PATH (E2E DB reset), .NET 10 SDK, Node 24.

| Suite | Command | Notes |
|---|---|---|
| Unit + integration | `dotnet test StockAccount.slnx` | Integration tests create `StockAccountTests_{guid}` (EF migrate + seed by the API itself), use the in-memory distributed cache and drop the DB at the end. Override server with env `TEST_SQL_CONNECTION` (`{guid}` placeholder supported). `KEEP_TEST_DB=1` keeps the DB; `RUN_KNOWN_BUGS=1` runs the skipped bug tests. |
| Postman | API running on 5080 (`dotnet run --project src/StockAccount.Api`), then `cd postman; npm ci; npm test` | `npm run test:docker` uses the docker environment. The collection JSON is generated by `node build-collection.mjs` (edit that file). Results JSON: `postman/results/` (git-ignored). Env var `runKnownBugs=true` turns on the "Known bugs" folder. |
| E2E | `cd e2e; npm ci; npm run test:vue` / `npm run test:nuxt` / `npm test` (both) | Playwright `webServer` starts everything: `scripts/start-api.mjs` **drops `StockAccountDb_E2E`**, then runs the API on **5081** (fresh migrate + seed, in-memory cache so ids never collide with the dev DB's Redis keys); vite dev on 5173 (`VITE_PROXY_TARGET=http://localhost:5081`); nuxt dev on 3000 (`NUXT_API_BASE=http://localhost:5081`). Ports 5173/3000/5081 must be free. HTML report: `npx playwright show-report`. |
| E2E against an existing stack | `npm run test:docker` (= `E2E_EXTERNAL=1`, `@smoke` tests) | Uses `E2E_VUE_URL` (default 5173) / `E2E_NUXT_URL` (default 3000) or `E2E_BASE_URL`. |
| Docker | `docker stop sa-redis; copy .env.example .env; docker compose up -d --build` → wait for `/health` → `cd postman; npm run test:docker` → `cd e2e; npm run test:docker` → `docker compose down -v; docker start sa-redis` | |

## 3. Test inventory

### 3.1 Integration (`tests/StockAccount.IntegrationTests`, 104 tests)
Infrastructure: `ApiFactory` (WebApplicationFactory<Program>, settings via `UseSetting`: per-run DB, `Cache:UseRedis=false`, `Reconciliation:Enabled=false`), `ApiFixture` (collection fixture: one DB per run, cached sessions per role, extra hosts on the same DB with different settings), `ApiSession` (manual `sa_refresh` cookie handling to inspect Set-Cookie and replay old tokens; ProblemDetails assertions check status, `application/problem+json`, `code`, `traceId`, `correlationId`), `TestData` (unique codes, invoice builders). All classes run sequentially in one collection; every test creates its own uniquely coded data.

| Class | Tests | Covers (CONTRACT §11.2 #) |
|---|---|---|
| `AuthTests` | 14 | #1: login ok (cookie HttpOnly/SameSite=Strict/Path=/, Expires, no refresh token in body), INVALID_CREDENTIALS, 400, `/auth/me` per role, rotation, replay inside grace (401, family kept), replay after grace with `RefreshReuseGraceSeconds=0` (401 + new token revoked, `RevokedReason` "Reuse detected" in DB), no/garbage cookie, 415 without JSON content type, logout deletes cookie + later refresh 401 + idempotent, lockout after 5 failures (ACCOUNT_LOCKED), USER_INACTIVE, deactivation revokes refresh, change password, tampered bearer 401 |
| `AuthorizationMatrixTests` | 22 (theory) | #2: anonymous 401 + admin/operator/portal on 22 endpoints (403 FORBIDDEN ProblemDetails) |
| `TenantIsolationTests` | 4 | #3: company B operator → 404 on Demo stock/account/invoice/transactions/PUT, empty lists, same code allowed in other company, X-Company-Id ignored for non-admins, cross-company invoice refs → 422 INVALID_REFERENCE, dashboard scoped; admin header filtering / all companies; COMPANY_REQUIRED; bad header 400 |
| `CrudTests` | 7 | #4: companies (DUPLICATE_TAX_NUMBER, COMPANY_NOT_EMPTY incl. company with only a user, soft delete, tax number reuse), users (role/company rules, weak password, DUPLICATE_EMAIL, ACCOUNT_IN_USE, link to account → portal user, CANNOT_DELETE_SELF), stocks (code normalization, balance never accepted, DUPLICATE_CODE, regex, bad enum, rowVersion required, soft delete + code reuse), accounts, invoices (validation keys `lines[0].quantity` etc., 0/201 lines, dueDate, 4-decimal qty, client totals ignored, 3 × 33.3333 @20 % = 100/20/120, PUT replaces all lines, type immutable, series normalization, 0.005 → 0.01 midpoint rounding, hard delete + line cascade), INACTIVE_STOCK / ACCOUNT_TYPE_MISMATCH on save |
| `PagingTests` | 18 | #5: envelope, totalCount/totalPages, page beyond end, `sort=code/-code/-unitPrice,code`, case-insensitive `q`, unit filter, default page size 20, 400 for pageSize 0/101/500, page 0, unknown sort on 8 list endpoints (incl. `act-trans?sort=quantity`), invoice filters (type, date range, series, q, status) and default sort `-invoiceDate,-id` |
| `InvoiceLifecycleTests` | 8 | #9–11: **Ahmet/Elma** (Draft 100/1/101, number null; approve → `SAT\d{6}`; exactly one StockTrans Out 2.000 / 50 / 100.00 and one ActTrans Debit 101.00 checked via API **and** SQL; stored balances ELMA −2 / C001 +101 in API and DB; sub-resources; portal sees it; INVOICE_NOT_DRAFT on PUT/DELETE/re-approve; operator cancel 403; admin cancel → reversal rows In/Credit `isReversal`, balances restored, INVOICE_NOT_APPROVED on 2nd cancel, original rows untouched); sequential numbering per series without gaps (deleted draft takes no number); ALS for purchases; INSUFFICIENT_STOCK (two lines of the same stock, detail lists code, nothing posted; exact balance allowed; cancel of purchase that would go negative); INACTIVE_ACCOUNT / ACCOUNT_TYPE_MISMATCH / INACTIVE_STOCK / INVOICE_HAS_NO_LINES at approve; zero-total invoice (StockTrans only, no ActTrans, also on cancel); stale rowVersion on approve 409; dashboard shape |
| `ConcurrencyTests` | 5 | #6 + §3.6: stale PUT 409 (stock, account), 5 parallel PUTs → 1×200 + 4×409, **parallel approvals draining one stock** (2 × 6 on 10 → exactly one OK + INSUFFICIENT_STOCK; 6 × 3 on 10 → ≤ 3 OK, never oversold, stored = ledger, no duplicate numbers), parallel approvals with one series → gap-free unique numbers |
| `CacheAndETagTests` (+ `CacheCompanyAndSearchTests`) | 13 | #7–8: `X-Cache` MISS→HIT, invalidation on PUT/create/delete, normalized query hash (param order), draft CRUD keeps cache, approve/cancel invalidate stock+account by-id and list entries for company **and** `all` scope, tenant check on cached by-id entry, company rename refreshes `companyName`, ETag weak 16-hex + `Cache-Control: private, no-cache` + 304 empty body on 5 endpoints, ETag changes after mutation, no ETag on `/api/auth/**`; Turkish-collation search of codes with `I` |
| `DeleteReconciliationPortalTests` | 5 | #12–13: STOCK_HAS_BALANCE, STOCK_IN_USE, ACCOUNT_IN_USE (draft), ACCOUNT_HAS_BALANCE, 204 → 404, history survives soft delete; reconciliation 0 → tamper `Stocks.Balance` via SQL → 1 discrepancy (exact stored/ledger/difference) → `fix=true` corrects DB and API → 0; account tampering; ledger immutability triggers (50001); portal sees only own non-draft data |
| `HealthAndErrorTests` | 7 | #14: test DB actually used, `/health` 200 with sql check, `X-Correlation-Id` echoed/generated and in ProblemDetails, unknown route 404 problem, validation problem shape, seed (ALS000001, S001 ≤ −18 714, 5 stocks), enums as strings / ISO dates |
| `KnownBugTests` | 2 | BUG-1, BUG-2 regression (skipped in round 1, active since the fix) |
| `SwaggerTests` | 44 | BUG-3 regression: each of the 43 contract operations has a 2xx response and the right auth (anonymous vs bearer + 401); Bearer scheme; typed `StockDto`/`InvoiceDto` schemas |
| `TimeAndScheduleTests` | 4 | access token expires after 15 min (shifted `TimeProvider`), refresh token after 7 days, lockout ≈ 5 min and released after `LockoutEnd`, reconciliation BackgroundService runs after `InitialDelaySeconds` and auto-fixes |
| `RedisOutageTests` | 1 | real Redis container stopped at runtime: requests keep working, `/health` Degraded, recovery after restart; latency measured (BUG-6) |

### 3.2 Postman (`postman/StockAccount.postman_collection.json`, v2.1, 11 folders / 113 requests)
Auth (12) · Companies (11) · Users (7) · Stocks (17) · CurrentAccounts (8) · Invoices (25) · Transactions (6) · Portal (6) · Admin (4: reconciliation, operator 403, `/health`, swagger.json) · **Scenario – Ahmet Elma** (14 ordered requests: read ELMA/C001 → Draft 100/1/101 → approve `SAT\d{6}` → exactly one Out 2 / one Debit 101 → balances −2 / +101 → portal sees it → admin cancel → balances restored → reversal rows) · Regression – fixed bugs (3: BUG-1, BUG-2, BUG-3; always run since round 2).
Collection-level pre-request script logs in as admin when `accessToken` is empty/expiring and stores it; the refresh token lives only in newman's cookie jar (Auth folder demonstrates refresh rotation, 415, logout, refresh-after-logout 401). Tokens/ids are captured into collection variables; every create uses unique codes so the collection is re-runnable against the same DB (verified: two consecutive local runs + one docker run green). Environments: `StockAccount.local.postman_environment.json`, `StockAccount.docker.postman_environment.json` (baseUrl `http://localhost:5080/api`, `hostUrl`, seed credentials).

### 3.3 E2E (`e2e/`, Playwright, Chromium; projects `vue` and `nuxt`, 21 tests each)
| Spec | Tests | Covers (CONTRACT §11.4 #) |
|---|---|---|
| `auth.spec.ts` | 6 | #1 invalid login → `login-error`, admin → "Gösterge Paneli", full admin menu + company switcher, logout → `/login`, guard `?redirect=`; redirect honoured after login; #7 operator menu (no companies/users/reconciliation/switcher), `/companies` → `/403`; #6 portal → `/my-account`, `my-balance`, no `nav-stocks`, `/stocks` → `/403`; logged-in `/login` redirect, 404 page; BUG-4 responsiveness check |
| `crud.spec.ts` | 6 | #2 stock CRUD as admin in Demo (create with Select/InputNumber, lower-case code normalized, search, edit, delete via "Silme Onayı"/"Evet", empty message), duplicate code error toast, "Tüm Firmalar" warning + disabled "Yeni", #3 account CRUD (type change shows "Müşteri/Tedarikçi"), no delete button for operator, #8 search "EL" → only ELMA, URL query sync, paginator page 2 (10 + 2 rows), unit filter |
| `invoice.spec.ts` | 4 | #4 Ahmet/Elma in the UI: AutoCompletes (account + stock), price/VAT pre-fill, live totals `data-value` 100/1/101, save → "Taslak", approve confirm text → "Onaylandı" + `SAT\d{6}`, read-only; ELMA `stock-balance` −2 (and in "Hareketler" tab), C001 `account-balance` +101, Stok Hareketleri row "Çıkış", Cari Hareketleri "Borç"; #5 operator has no cancel button, admin cancels with reason → "İptal Edildi", balances restored; insufficient stock → error toast "Hata" mentioning ELMA, draft deleted; account AutoComplete filtered by type, multi-line totals 810 / 150.6 / 960.6, line remove |
| `admin.spec.ts` (round 2) | 5 | companies CRUD + switcher, duplicate tax number / non-empty delete toasts, users CRUD (portal user linked to an account logs in → `/my-account`), duplicate e-mail, reconciliation screen run → tamper → 1 discrepancy → fix → 0 |
| `ui.spec.ts` (round 2) | 2 | dark mode toggle + persistence (Nuxt: SSR HTML has `app-dark`); proactive token refresh with `page.clock` |
| `mobile.spec.ts` (round 2, projects `vue-mobile`/`nuxt-mobile`, Pixel 7) | 1 | login, collapsed menu + toggle, list search, invoice editor totals, no horizontal overflow |
| `session-cache.spec.ts` | 5 | #9 no token in localStorage/sessionStorage (checked against the real token), `sa_refresh` httpOnly/Strict/Path=/ and not in `document.cookie`, reload of `/stocks` keeps the session (Vue: POST `/auth/refresh` observed), `clearCookies` + reload → `/login`; portal sees only own invoices and correct balance; #10 Dashboard→Stocks→Dashboard→Stocks→… issues ≤ 1 `GET /api/stocks`, after creating a stock the list is requested again, "Yenile" bypasses cache; Nuxt: SSR-loaded list makes **0** browser data requests; #11 Nuxt SSR: `/login` HTML has `login-email`, `/stocks` HTML (with cookies) contains `stock-table` and data rows, anonymous SSR `/stocks` → 302 to `/login` |

## 4. Contract scenario coverage

| Contract item | Integration | Postman | E2E |
|---|---|---|---|
| §11.2 #1 Auth (cookie, rotation, reuse, grace, logout, change password) + lockout | ✔ | ✔ (no reuse) | ✔ (session) |
| #2 Policy matrix | ✔ | ✔ | ✔ (menus/guards) |
| #3 Tenant isolation | ✔ | – | – |
| #4 CRUD + validation | ✔ | ✔ | ✔ (stock, account) |
| #5 Paging/sort/filter | ✔ | ✔ | ✔ |
| #6 Concurrency (stale 409, parallel approvals no oversell) | ✔ | ✔ (stale) | – |
| #7 Cache HIT/MISS + invalidation | ✔ | ✔ | ✔ (UI cache) |
| #8 ETag/304 | ✔ | ✔ | – |
| #9 Ahmet/Elma | ✔ (API + SQL) | ✔ | ✔ |
| #10 Rules / numbering / cancel | ✔ | ✔ | ✔ |
| #11 Purchase | ✔ | ✔ | – |
| #12 Delete rules | ✔ | ✔ (COMPANY_NOT_EMPTY) | – |
| #13 Reconciliation | ✔ (tamper + fix) | ✔ (run) | – |
| #14 Health, correlationId | ✔ | ✔ | – |
| Zero-total invoice (B5) | ✔ | – | – |
| §11.4 #1–#11 | – | – | ✔ all (vue + nuxt; #11 Nuxt only) |

## 5. Bugs

| ID | Severity | Owner | Title | Status |
|---|---|---|---|---|
| BUG-1 | Medium | Backend | `GET /api/companies/current` returns 403 for operators | **Verified** (d918312): integration `KnownBugTests` and matrix (operator ok), newman regression |
| BUG-2 | Low | Backend | JSON/model-binding 400s are not `application/problem+json` and leak English framework messages | **Verified**: strict problem+json in `CrudTests` and `KnownBugTests`, newman regression |
| BUG-3 | Low | Backend | Swagger gaps: missing 2xx responses on 28 operations, Bearer required on anonymous auth endpoints | **Verified**: `SwaggerTests` (43 operations: 2xx and auth), newman regression |
| BUG-4 | **High** | Frontend (ui-nuxt) | Page freezes (main thread blocked) after logout that follows a client-side login | **Verified** (f5be41d): E2E regression on nuxt dev and on the Docker image; workarounds removed |
| BUG-5 | Low | Backend | Request-completion log lines have an empty `CompanyId` | **Verified**: the last 400 operator request lines in `logs/api-20261022.log` all show `[U:2] [C:1]` |
| BUG-6 | Medium | Backend | When Redis goes down at runtime, every cached read and write waits for the Redis timeouts (4–8 s per request) | **Verified** (9b9c963): circuit breaker + 1 s Redis timeouts; `RedisOutageTests` now asserts cached GETs < 1 s during the outage (measured: 4–8 s → 1–45 ms) and no stale data after recovery |

### BUG-1 — Operator gets 403 on `GET /api/companies/current` (Medium, Backend)
- Repro: `POST /api/auth/login` as `demo@stockaccount.local / Demo123!` → `GET /api/companies/current` with the bearer token.
- Expected (§5.5): 200 `CompanyDto` of the user's company (policy **Operator**).
- Actual: 403 `FORBIDDEN`. Admin works.
- Cause (suspected): `CompaniesController` has class-level `[Authorize(Policy = AdminOnly)]` and the action adds `[Authorize(Policy = Operator)]`. ASP.NET Core **AND-combines** the two, so a non-admin always fails. Fix: move `AdminOnly` to the individual actions, or move `current` to another controller.
- Tests: `KnownBugTests.Operator_companies_current_returns_own_company` (skipped) and Postman "Known bugs" folder (skipped). Both UIs define `companies.current()` in their API modules but no page calls it, so there is no visible UI impact yet.

### BUG-2 — Model-binding errors are not contract ProblemDetails (Low, Backend)
- Repro: `POST /api/stocks` as operator with body `{"code":"ZZ1","name":"x","unit":"Ton","vatRate":1,"unitPrice":1,"isActive":true}`. Malformed JSON (`{"code":`) gives the same result.
- Expected (§5.2): `Content-Type: application/problem+json`, Turkish messages, `errors` keyed by camelCase property path.
- Actual: status 400 and `code: VALIDATION_FAILED` are correct, but:
  - the response is `Content-Type: application/json; charset=utf-8`;
  - `errors.unit` is `"The JSON value could not be converted to StockAccount.Domain.Enums.StockUnit. Path: $.unit | LineNumber: 0 | BytePositionInLine: 37."`, which leaks the type name and is in English;
  - there is an extra key `errors.request: ["The request field is required."]`.
- Cause (suspected): `InvalidModelStateResponseFactory` returns a `BadRequestObjectResult` whose `ContentTypes` setting is not honoured, and the default binder messages are not localized. FluentValidation 400s are correct.
- Test: `KnownBugTests.Json_binding_errors_are_problem_json` (skipped).

### BUG-3 — Swagger documentation gaps (Low, Backend)
All 42 contract `/api` operations are present, the Bearer scheme exists and the `X-Company-Id` header parameter is documented. Gaps:
1. 28 operations document **no 2xx response or schema**, only 400/401/403(/404/409/422). Examples: `GET /api/stocks`, `GET /api/stocks/{id}`, `PUT /api/stocks/{id}`, `GET /api/invoices/{id}`, `POST /api/invoices/{id}/approve`, `POST /api/invoices/{id}/cancel`, all `/api/me/*`, `/api/dashboard/summary`, `/api/admin/reconciliation/run`. The base-class `[ProducesResponseType]` attributes probably suppress the inferred `ActionResult<T>` 200.
2. Security is a **global** requirement, so the anonymous `POST /api/auth/login`, `/refresh` and `/logout` are also shown as requiring Bearer.
3. The policy (Admin/Operator/Portal) appears only in some summaries, e.g. "Admin only" on DELETE stock and on cancel.
4. `/health` is not in the document. This is acceptable because it is not an `/api` route.

Expected: every operation lists its success response type and its auth requirement (anonymous vs. bearer and policy).

### BUG-4 — ui-nuxt freezes after logout (High, Frontend / ui-nuxt)
Reproduced on `nuxt dev` and on the Docker production image; ui-vue is not affected.

Steps:
1. Open `http://localhost:3000/login` (SSR) and log in as `demo@stockaccount.local / Demo123!`. This is a client-side login with client navigation to `/`.
2. Wait for "Gösterge Paneli" to appear.
3. Click `logout-button`.

Expected: the `/login` page is shown and usable.

Actual:
- The URL changes to `/login`, but the page's main thread stays blocked. `page.evaluate()` never returns, and inputs cannot be filled for > 60 s.
- Only a full reload or navigation recovers it.
- Just before the freeze, a `GET /api/dashboard/summary` without a token is fired, and the console shows several 401s from refresh/data requests.
- When the page was loaded by SSR *after* login (e.g. a full reload of `/` or `/stocks`) and then logged out, it does not freeze, although the extra 401 requests still appear.

Suspected cause: the global middleware runs `auth.bootstrap()` on `/login` while the dashboard's `useCachedAsyncData` re-runs after `clear()`, which ends in a synchronous reactive or redirect loop. Pages should not refetch after logout.

Tests:
- `auth.spec.ts` "page stays responsive after logout…" is marked `test.fail` on Nuxt and must be un-marked after the fix.
- The admin logout test on Nuxt does a full load before logout to avoid this bug.

### BUG-5 — `CompanyId` missing on request log lines (Low, Backend)
- Expected (§7): log lines carry `CompanyId`.
- Actual: `logs/api-*.log` request-completion lines (`HTTP GET /api/stocks responded 200`) show `[U:2] [C:]` for the Demo operator. Locally 1 190 lines have an empty `C`, and only the 45 business-event lines have `[C:1]`. This was seen locally and in Docker.
- Cause (suspected): `UseSerilogRequestLogging` sits outside `UserLogContextMiddleware`. `UserId` is added through `EnrichDiagnosticContext`, but `CompanyId` is not. Fix: also call `diagnostics.Set("CompanyId", …)`.

### BUG-6 — Redis outage at runtime makes cached endpoints very slow (Medium, Backend)
Repro (automated in `RedisOutageTests`):
1. Start the API with `Cache:UseRedis=true` against a Redis container.
2. Warm the cache with `GET /api/stocks/{id}` (MISS, then HIT).
3. Stop the container (`docker stop`).
4. Repeat the requests.

What works as the contract requires: every request still succeeds, `X-Cache: MISS`, data is fresh, PUT and approve work, `/health` → 200 `Degraded` (redis Degraded, sql Healthy), and the cache recovers after `docker start`.

The problem is latency:

| Request | Time with Redis down |
|---|---|
| `GET /api/stocks/{id}` | 4.0–4.3 s |
| `GET /api/stocks?pageSize=5` | 8.0 s |
| Not-cached `GET /api/invoices` (comparison) | 74 ms |
| Outage phase, 7 requests | 52 s |

- Expected: after the first failure the API should stop waiting on Redis quickly, for example with a circuit breaker / short-lived "Redis down" flag in `DistributedCacheService`, or a much lower timeout. CONTRACT §6 only says failures must not fail a request; it says nothing about latency. The runtime fallback (in-memory cache used when Redis drops) is unspecified, so this is rated Medium for performance.
- Cause (suspected): each cache operation waits for the 2 s StackExchange.Redis sync/async timeout. A versioned list read does 2–4 operations: version get/set and list get/set.
- The test only asserts that the outage phase takes < 60 s, so it stays green. Tighten that limit after the fix.

### Observations (not bugs)
- Under heavy contention (6 parallel approvals on one stock), some approvals end with 409 `CONCURRENCY_CONFLICT` after the 3 retries instead of `INSUFFICIENT_STOCK`. This is contract-conformant (§3.6), and the stock is never oversold; clients should simply retry. With 2 contenders it is always one OK and one INSUFFICIENT_STOCK.
- `GET /api/me/invoices?status=Draft` → 400, because only Approved/Cancelled are accepted. This is an acceptable reading of §5.10.
- Contract §11.4 #8 says "search EL shows ELMA only". This holds only while no other stock code or name contains "EL". The E2E data avoids "EL".
- After many E2E runs ELMA moves to page 2 of the default code-sorted list, because `E2E…` codes sort before it. The session, cache and SSR tests therefore assert `ARMUT`, which is always on page 1.
- The Frontend Dockerfiles (ui-vue nginx, ui-nuxt node) built and passed health checks on the first real build, so there are no Frontend Docker bugs. The Redis keys `sa:stock:list:{scope}:{ver}:{hash}` were verified in the compose Redis, and logs in `/app/logs`.

## 6. Not tested / limits

Round 2 closed these gaps:
- **Redis down at runtime**: `RedisOutageTests` uses its own container `sa-redis-it`:6390, never the dev `sa-redis`. It is skipped automatically when docker is not available (`DOCKER_EXE` overrides the binary path).
- **Access-token expiry (15 min)** and **refresh-token expiry (7 days)**: `TimeAndScheduleTests` replaces the DI `TimeProvider` with a shiftable clock.
- **Lockout**: duration ≈ 5 min is asserted from `LockoutEnd`, and expiry is simulated by moving `LockoutEnd` into the past, because Identity uses the system clock.
- **Reconciliation BackgroundService schedule**: tested with `InitialDelaySeconds=1` and `AutoFix=true`; the tampered balance is fixed by the service itself.
- **UI proactive refresh**: Playwright `page.clock.fastForward('14:30')` makes the client refresh before the 60 s limit.
- **Admin screens E2E** (`admin.spec.ts`, 5 tests per UI): companies CRUD, users CRUD incl. a portal user that can log in, and the reconciliation screen with tamper → detect → fix. The tamper step needs local SQL and is skipped with `E2E_EXTERNAL=1`.
- **Mobile** (`mobile.spec.ts`, Pixel 7 projects `vue-mobile`/`nuxt-mobile`): login, the collapsed sidebar toggle, list, the invoice editor and "no horizontal overflow".
- **Dark mode** (`ui.spec.ts`): toggle, persistence across reload, and Nuxt SSR rendering `app-dark` from the `sa_theme` cookie.

Still not tested:
- The reconciliation interval itself: it is at least 1 min by code, and only the first run is asserted.
- Lockout expiry through real time passing, because Identity does not use the injected `TimeProvider`.
- Cross-site SameSite behaviour (first navigation from another site) and real iOS Safari, since the mobile run is Chromium device emulation.
- Performance and load testing beyond the parallel approval tests and the BUG-6 latency measurement.

## 7. Requirement traceability (PLAN §6)

| # | Requirement | Verified by | Status |
|---|---|---|---|
| R1 | Stock services | Int `CrudTests`, `PagingTests`; Postman Stocks | ✔ |
| R2 | Current account services | Int `CrudTests`; Postman CurrentAccounts | ✔ |
| R3 | Invoice/Fiş services | Int `InvoiceLifecycleTests`; Postman Invoices | ✔ |
| R4 | Admin / User authorization | Int `AuthTests`, `AuthorizationMatrixTests`; E2E auth | ✔ (BUG-1) |
| R5 | CRUD for all entities | Int `CrudTests` (company, user, stock, account, invoice + lines); ledgers read-only by design | ✔ |
| R6 | Lists with paging/filtering | Int `PagingTests`; E2E paging/filter | ✔ |
| R7 | Stock & account cached in Redis | Int `CacheAndETagTests` (X-Cache); Docker: `sa:*` keys in Redis | ✔ |
| R8 | Cache refresh on CRUD / balance change | Int cache invalidation (CUD, approve/cancel, company rename) | ✔ |
| R9 | User-friendly UI | E2E (toasts, confirmations, AutoComplete, live totals, validation, guards) | ✔ (BUG-4 on Nuxt) |
| R10 | UI caches HTTP requests | E2E #10 (≤ 1 request within TTL, refetch after mutation, Nuxt SSR 0); Int ETag/304 | ✔ |
| R11 | Admin controls base entities | Int matrix (companies/users AdminOnly, delete stock/account, cancel) | ✔ |
| R12 | Company is the main entity | Int `TenantIsolationTests` | ✔ |
| R13 | Company can be a user (operator) | Int tenant tests (operator B) | ✔ |
| R14 | Stock in invoice lines | Int/E2E invoice tests | ✔ |
| R15 | Current account = counterparty, can be a user (portal) | Int portal tests, user→account link; E2E portal | ✔ |
| R16 | Invoice header + lines | Int `CrudTests` invoice | ✔ |
| R17 | Invoice created as Draft | Int, Postman, E2E | ✔ |
| R18 | Approval creates stock & account movements | Int (API + SQL), Postman scenario, E2E | ✔ |
| R19 | Ahmet / 2 kg Elma | Int `Ahmet_buys_2kg_elma…`, Postman scenario folder, E2E invoice spec | ✔ |
| R20 | Balances stored on stock/account | Int (DB balances, ledger = stored after concurrency), reconciliation | ✔ |
| R21 | .NET (10) | build + tests | ✔ |
| R22 | MSSQL | Int on SQLEXPRESS; Docker mssql | ✔ |
| R23 | Redis | Docker stack (Healthy, keys present) | ✔ |
| R24 | Serilog | log files with correlation id (local + Docker) | ✔ (BUG-5) |
| R25 | PrimeVue + PrimeFlex | E2E on PrimeVue components (Select, AutoComplete, InputNumber, DataTable, Toast, ConfirmDialog) | ✔ |
| R26 | Postman | newman 113/341 green, local + Docker | ✔ |
| R27 | Swagger | swagger.json check (§1, BUG-3) | ✔ (gaps) |
| R28 | Vue.js project | E2E project `vue` | ✔ |
| R29 | Nuxt migration (SSR) | E2E project `nuxt` incl. SSR HTML/0-request tests | ✔ (BUG-4) |
| R30 | Dockerize | compose build + up, newman + Playwright smoke on containers | ✔ |
| R31 | Extra (clean architecture, migrations, JWT refresh, BackgroundService, ProblemDetails, FluentValidation, seed, tests) | all suites | ✔ |
