# Backend Notes (owner: Backend agent)

Companion to `docs/CONTRACT.md` (v1.1) and `docs/DB_NOTES.md`. Describes what the API provides, how to run it, and every
decision or deviation the other agents need to know.

## 1. How to run (local)

```powershell
# Redis (optional; the API falls back to an in-memory cache and /health reports "Degraded")
docker run -d --name sa-redis -p 6379:6379 redis:7-alpine      # or: docker start sa-redis

dotnet tool restore
dotnet build StockAccount.slnx                                   # 0 warnings / 0 errors (warnings are errors in src/)
dotnet run --project src/StockAccount.Api                        # http://localhost:5080 (launch profile "http")
```
- The first start migrates `StockAccountDb` on `.\SQLEXPRESS`, seeds (CONTRACT §9) and approves the seeded Draft opening
  purchase invoice through `IInvoiceService.ApproveAsync` (→ `ALS000001`, ELMA balance 100, S001 balance −18,714.00).
  Re-running is idempotent.
- Swagger UI: http://localhost:5080/swagger (JWT "Authorize" button; paste the `accessToken` from `POST /api/auth/login`).
  OpenAPI JSON: `/swagger/v1/swagger.json`. Health: http://localhost:5080/health.
- Logs: console + `logs/api-YYYYMMDD.log` relative to the process working directory (`src/StockAccount.Api/logs/` under
  `dotnet run --project`, `/app/logs` in Docker). Every line carries the correlation id.
- Unit tests: `dotnet test tests/StockAccount.UnitTests` (90 tests).
- Docker: `copy .env.example .env` then `docker compose up --build -d` (API on 5080). Stop the local `sa-redis` container
  first, the compose `redis` service binds 6379 too.

Seed logins: `admin@stockaccount.local / Admin123!` (Admin), `demo@stockaccount.local / Demo123!` (operator, Demo company),
`ahmet@stockaccount.local / Ahmet123!` (portal user for account C001).

## 2. Architecture

```
Api            Controllers (thin) → Application services; HttpCurrentUser (ICurrentUser), JWT bearer + policies,
               GlobalExceptionHandler → ProblemDetails, ValidationFilter (FluentValidation), ETagMiddleware,
               CorrelationIdMiddleware, Serilog request logging, Swagger, /health, CORS, forwarded headers
Application    Features/{Auth,Companies,Users,Stocks,CurrentAccounts,Invoices,Transactions,Dashboard,Portal,Reconciliation}
               each with Dtos/, Validators/, I<Name>Service + <Name>Service; Common/{Exceptions,Paging,Caching,Security,
               Tenancy,Validation,Abstractions}. Pure invoice rules in Features/Invoices/InvoicePosting.cs.
Infrastructure Persistence (DB agent) + Identity (UserManager config, Turkish error texts), JwtTokenService,
               Caching (DistributedCacheService, NoOpCacheService, Redis probe/fallback), SqlServerDatabaseErrorInspector,
               ReconciliationService (DB views) + BalanceReconciliationService (BackgroundService), Startup/DatabaseInitializer
```
- Manual mapping (projections / `ToDto()`), no MediatR/AutoMapper. Tenant scoping is explicit via `ForTenant(ICurrentUser)`.
- Sorting uses the DB agent's `SortMap`/`ApplySort` whitelists (one static map per list), so no client string reaches SQL.
- Approve/cancel (CONTRACT §3.6, DB_NOTES §2.2–2.3): up to 3 attempts; each attempt clears the change tracker and runs inside
  `CreateExecutionStrategy().ExecuteAsync` with a ReadCommitted transaction; loads invoice + lines, stocks and account (tracked,
  `IgnoreQueryFilters([SoftDelete])`), checks the client rowVersion on attempt 1 only, applies `InvoicePosting`, **one**
  `SaveChanges`, commit. `DbUpdateConcurrencyException` or SQL 2601/2627 → retry; after 3 → 409 `CONCURRENCY_CONFLICT`.
  Cache invalidation after commit.
- History queries (invoice list/detail, ledgers, dashboard recent invoices, portal) use `IgnoreQueryFilters([SoftDelete])`.

## 3. Behaviour details the UIs and tests can rely on

| Topic | Behaviour |
|---|---|
| Auth cookie | `sa_refresh`: HttpOnly, SameSite=Strict, Path=/, Secure only when `Request.IsHttps`, Expires = refresh expiry (7 d). Login/refresh set it; logout and any failed refresh delete it. Never in a body. |
| Refresh / logout | `POST` with `Content-Type: application/json` and body `{}`. Missing JSON content type → **415** ProblemDetails (code `VALIDATION_FAILED`). |
| Rotation | Atomic (`UPDATE … WHERE RevokedAt IS NULL`); the loser of a concurrent rotation gets 401 `INVALID_REFRESH_TOKEN` without family revocation. Replaying a token rotated < `RefreshReuseGraceSeconds` ago → 401, no revocation; later → all active tokens of the user revoked ("Reuse detected") + 401. |
| Login errors | Unknown email / wrong password → `INVALID_CREDENTIALS`; locked (5 failures → 5 min) → `ACCOUNT_LOCKED` (checked before the password); inactive user → `USER_INACTIVE` (only after a correct password). |
| Admin company scope | Header `X-Company-Id` only (the API does not read the `sa_company` cookie; both UIs send the header, also from SSR). Non-integer header → 400 `VALIDATION_FAILED` (`errors["X-Company-Id"]`). Ignored for non-admins. |
| Validation errors | 400, `code: VALIDATION_FAILED`, `errors` keys camelCase paths (`lines[0].quantity`, `rowVersion`, `sort`, `pageSize`); `detail` = first message. JSON/model-binding errors use the same shape. |
| ProblemDetails | Always `type, title, status, detail, instance, code, traceId, correlationId`; 401/403 from the JWT middleware and unknown routes (404 `NOT_FOUND`) too. 500 → `INTERNAL_ERROR`, exception text only in Development. |
| Optimistic concurrency | PUT with a rowVersion different from the stored one → 409 `CONCURRENCY_CONFLICT` immediately (even if nothing else changed); a race between load and save is caught by the rowversion-guarded UPDATE. |
| JSON | camelCase, enums as strings, `DateOnly` `yyyy-MM-dd`, `DateTime` ISO-8601 UTC with `Z`, decimals as numbers with DB scale (e.g. `unitPrice: 50.0000`, `balance: 98.000`), `rowVersion` Base64. |
| X-Cache | `HIT`/`MISS` on `GET /stocks`, `/stocks/{id}`, `/current-accounts`, `/current-accounts/{id}`. |
| ETag | Weak ETag + `Cache-Control: private, no-cache` on 200 JSON GETs under `/api/**` except `/api/auth/**`; `If-None-Match` match → 304 empty body. |
| Health | `/health` JSON `{status, checks:{sql, redis}}`. SQL unhealthy → 503. Redis fallback or Redis not answering → `Degraded` (200), because cache failures never fail requests. |
| Cache keys (Redis, prefix `sa:`) | `stock:{id}`, `stock:ver:{scope}`, `stock:list:{scope}:{ver}:{hash16}`, same for `account`. Verified in Redis after a run. |

## 4. Decisions / deviations

| # | Topic | Decision |
|---|---|---|
| B1 | `ICurrentTenant` (brief) | Folded into `ICurrentUser.CompanyId` exactly as CONTRACT §3.8/§4.3 (and DB_NOTES D1); no separate interface. |
| B2 | ETag "result/endpoint filter" | Implemented as middleware (`ETagMiddleware`) that buffers only the eligible GETs; same observable behaviour. |
| B3 | `/act-trans` sort "same sorts" | `quantity` does not exist on ActTrans, so act-trans sorts are `transDate, amount, createdAt` (+ `id`). |
| B4 | Invoice create/update | Besides `INVALID_REFERENCE` and `ACCOUNT_TYPE_MISMATCH`, inactive stock/account is rejected already on save with 422 `INACTIVE_STOCK` / `INACTIVE_ACCOUNT` (contract: line stock "same company, active"); approve re-checks everything. |
| B5 | GrandTotal 0 (DB_NOTES D5, coordinator) | Approval allowed; StockTrans written; ActTrans skipped. Cancel writes a reversing ActTrans only if the approval wrote one. |
| B6 | Refresh of an inactive user / expired token | 401 `INVALID_REFRESH_TOKEN` (endpoint table) rather than `USER_INACTIVE`. Deactivating a user revokes his refresh tokens. |
| B7 | Redis health | Unhealthy Redis is reported as `Degraded` (200), not 503 (see §3). Package `AspNetCore.HealthChecks.Redis` is used when Redis is active; with the fallback a static Degraded check is registered. |
| B8 | Config additions | `Reconciliation:InitialDelaySeconds` (default 30, first run delay per §3.9). `appsettings.Docker.json` (CORS/DB flags). |
| B9 | Unit tests | Written by Backend per coordinator instruction in `tests/StockAccount.UnitTests` (added NSubstitute + EF Core SQLite). The SQLite test context strips SQL-Server CHECK constraints (decimals are TEXT in SQLite) and fakes rowversion values. |
| B10 | Schema | No schema change and no new migration were needed. |
| B11 | Swagger | Swashbuckle.AspNetCore 10.3.1 (Microsoft.OpenApi 2.x) works on net10.0; Bearer scheme + optional `X-Company-Id` header parameter on authorized non-auth operations; XML comments enabled. |
| B12 | Dashboard | "This month" by InvoiceDate in the current UTC month, Approved only; `lowestStocks` = 5 lowest balances among active stocks; `recentInvoices` = last 5 by createdAt (all statuses). |

## 5. Requests / hints for other agents

- **Frontend:** send `X-Company-Id` (also from Nuxt SSR) — the API does not read `sa_company`. `/auth/refresh` and `/auth/logout`
  need `Content-Type: application/json` and `{}`. Approve/cancel need the latest `rowVersion` (take it from the last GET/PUT
  response). Validation error keys are camelCase paths such as `lines[0].quantity`.
- **Test (integration):** `WebApplicationFactory<Program>` works (`public partial class Program`). Recommended overrides:
  `Cache:UseRedis=false`, `Reconciliation:Enabled=false`, `Jwt:RefreshReuseGraceSeconds=0` for the reuse test,
  `Database:MigrateOnStartup=true` + `Database:Seed=true` with a per-run DB name (EF creates it with `Turkish_CI_AS`).
  Tampering `Stocks.Balance` from `sqlcmd` needs `-I` (QUOTED_IDENTIFIER ON; filtered indexes). Five wrong passwords lock a
  user for 5 minutes — use a throw-away user for lockout tests.
- **Test (Postman):** refresh/logout rely on the cookie jar (`sa_refresh`, Path=/).
- **Docker smoke (TS-5):** stop the local `sa-redis` before `docker compose up` (port 6379).

## 6. Fixes after TEST_REPORT (2026-10-22)

| Bug | Fix |
|---|---|
| BUG-1 | `CompaniesController`: `AdminOnly` moved from the class to each admin action, so `GET /companies/current` is reachable with `Operator` (class + action policies are AND-combined). Other controllers use class `Operator` + action `AdminOnly`, which correctly yields admin-only. |
| BUG-2 | `ModelStateProblemFactory`: JSON/model-binding errors → `application/problem+json`, `code: VALIDATION_FAILED`, Turkish messages ("Geçersiz değer veya biçim.", "İstek gövdesi geçerli bir JSON değil."), camelCase field keys, no framework text; the redundant `request` key is dropped (body-level errors use key `body`). |
| BUG-3 | `SuccessResponseTypeConvention` documents the 2xx schema of every `ActionResult<T>` action; Bearer security is applied per operation only to `[Authorize]` endpoints (no global requirement) and the effective policy is appended to the description. |
| BUG-5 | Request-completion log lines get `CompanyId` via `IDiagnosticContext` (claim, admin `X-Company-Id`, or `all`). |
| BUG-6 | Redis outage at runtime: `ResilientDistributedCache` + `CacheCircuitBreaker` (Infrastructure/Caching). The first Redis error opens the circuit for `Cache:CircuitBreakSeconds` (default 30). While it is open, Redis is not contacted and a process-local in-memory cache is used. This cache is a fresh instance per outage, so nothing from an earlier outage is served. After the break, one caller runs the half-open probe: PING, then delete every `sa:*` key, then close the circuit. The flush is needed because invalidations made during the outage only reached the local cache, and Redis can reload pre-outage entries from its dump. Redis timeouts are now 1000 ms with `BacklogPolicy.FailFast`; the connection strings in appsettings/compose use `connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000`. This is a deviation from the contract's `connectTimeout=2000` and was requested by the coordinator. Measured during the outage: GET by id 4.0–4.3 s → 16 ms first / 1 ms next; list 8.0 s → ~45 ms; outage phase 52 s → ~0.4 s. |
