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

## 7. Production readiness

### 7.1 Rate limiting
- `AddRateLimiter` + `UseRateLimiter()` (after authentication). The **global** limiter is a sliding window partitioned per
  authenticated user id (`u:{sub}`), otherwise per client IP (`ip:{addr}`). It applies to every endpoint except `/health*`.
- Policy **`auth`** (`[EnableRateLimiting]` on `POST /api/auth/login` and `/refresh`) is a much stricter limit per client IP.
- Rejection: **429** `application/problem+json`, `code: RATE_LIMITED` (new code, not in CONTRACT §5.2), Turkish detail
  ("Çok fazla istek gönderildi. Lütfen N saniye sonra tekrar deneyin."), and a `Retry-After` header.
- Config `RateLimiting`:

  | Environment | Global (per user/IP) | Auth (per IP) |
  |---|---|---|
  | defaults / Production (`appsettings.json`) | 300 / 60 s | 10 / 60 s |
  | Docker demo | 1000 / 60 s | 100 / 60 s |
  | Development | 3000 / 60 s | 300 / 60 s |
  | Testing | disabled (rate-limit tests enable it per factory) | |

- **Client IP behind proxies:** `ForwardedHeaders:KnownNetworks/KnownProxies/ForwardLimit` (default `ForwardLimit=1`).
  - An empty list trusts loopback only.
  - Docker trusts the private ranges of the compose network.
  - Production must list the real proxies. `0.0.0.0/0` is rejected in strict environments.
  - nginx and the Nuxt proxy must send `X-Forwarded-For`; that is the Frontend's part.

### 7.2 Fail-fast configuration
- `ConfigurationValidator` runs before any service registration. If anything is invalid it writes **one** fatal log line
  listing the keys and the process exits with **code 2**. Secret values are never logged.
- Checks:
  - `ConnectionStrings:Default`; `ConnectionStrings:Redis` when `Cache:UseRedis`.
  - `Jwt:Key` is present and at least 32 bytes.
  - Jwt issuer/audience, lifetimes, grace and cookie name.
  - CORS origins are absolute http(s) and not `*`.
  - Rate-limit values; forwarded-header CIDRs and IPs.
  - Cache TTLs; reconciliation interval; `Database:CommandTimeoutSeconds`.
- `ValidateOnStart` options are added on top (Jwt, Cache, RateLimiting). An `OptionsValidationException` is also logged as
  one line with exit code 2.
- Placeholder secrets (`change-me`, `DEV-ONLY`):
  - **error** in strict environments (everything except Development, Testing and Docker);
  - **warning** in `Docker`, which is the local demo using `.env.example`;
  - accepted in Development and Testing.
- Verified:
  - `ASPNETCORE_ENVIRONMENT=Production dotnet StockAccount.Api.dll` → `FTL Invalid configuration … Jwt:Key is a placeholder value …`, exit 2.
  - An empty `ConnectionStrings__Default` → `… ConnectionStrings:Default is missing`, exit 2.
- Compose uses `${JWT_KEY:?…}` and `${MSSQL_SA_PASSWORD:?…}`, so `JWT_KEY= docker compose config` fails with exit 1.
- `scripts/deploy/new-secrets.ps1` writes strong values into `.env` and never prints them:
  - `JWT_KEY`: 64 random bytes, Base64Url.
  - `MSSQL_SA_PASSWORD`: 28 characters that satisfy the complexity policy.
  - `-Keys JWT_KEY` rotates only the JWT key. The SA password is fixed when the mssql volume is created.

### 7.3 Error handling hardening
- `UseExceptionHandler` + `UseStatusCodePages` + `ModelStateProblemFactory` → every error is `application/problem+json`,
  including 404/405 for unknown routes and methods.
- Exception text appears only in Development (`exception` extension). Everywhere else a 500 is `INTERNAL_ERROR` with a
  generic Turkish detail. An integration test in Production checks that no message, type name or stack frame leaks.
- The Kestrel `Server` header is off and `X-Powered-By` is removed.
- `SecurityHeadersMiddleware` sets `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  `Permissions-Policy` and `Cross-Origin-Resource-Policy: same-origin`. It also sets
  `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'` on everything except Swagger UI.
- HSTS (1 year) in Production. It is only emitted on HTTPS; TLS terminates at the proxy.
- Production also turns off Swagger and seeding (`appsettings.Production.json`).

### 7.4 Database pool and health
- Connection strings set `Max Pool Size=100;Min Pool Size=5;Connect Timeout=15` (appsettings + compose).
- The EF command timeout is `Database:CommandTimeoutSeconds` (default 30), applied through `ConfigureDbContext`. Retry on
  transient faults stays on (DB agent).
- **No `AddDbContextPool`:** the context options resolve the scoped `AuditSaveChangesInterceptor`, which depends on the
  per-request `ICurrentUser`. Pooled contexts need singleton options, so pooling would freeze the first request's user.
  SqlClient's connection pool is the effective pool.
- Health endpoints:
  - `/health/live`: no dependencies; always 200 while the process serves requests.
  - `/health/ready`: SQL plus Redis. SQL down → 503. Redis down or fallback → `Degraded` with 200, so the API stays ready.
  - `/health`: alias of ready.
  - JSON output has per-check `status` and `durationMs`; `description` appears only in Development.
  - Health endpoints are not rate limited.
- Docker healthcheck for `api`: the aspnet image has no curl/wget, so `bash` `/dev/tcp` sends `GET /health/live` and
  expects a 200. Verified: `stockaccount-api-1 … (healthy)`.
- Database initialization (schema guard, migrate, seed, opening-invoice approval) now runs in
  `IHostedLifecycleService.StartingAsync`, before Kestrel listens. A failure aborts the start.

### 7.5 Versioned deploy, rollback, backups
- Images:
  - App images are tagged `stockaccount-{api,ui-vue,ui-nuxt}:${APP_VERSION:-local}`. `deploy.ps1` also adds `:sha-<git sha>`.
  - `mssql` and `redis` are pinned by digest: SQL Server 2022 16.0.4305.5 and Redis 7.4.11-alpine.
- `./backups:/var/opt/mssql/backup` is bind-mounted for the DB agent's `scripts/db/backup.ps1` / `restore.ps1`.
  `backups/` contains only `.gitkeep` and `.gitignore`; files are never committed.
- `scripts/deploy/deploy.ps1 -Version x.y.z` runs these steps:
  1. Pre-deploy backup (`backup.ps1 -Target docker -Label pre-deploy-x.y.z`). A failed backup aborts the deploy. It is
     skipped only for the very first deploy, when mssql is not running yet.
  2. Build and tag the images.
  3. Record the release in `scripts/deploy/.state/releases.json` (git-ignored).
  4. `compose up -d`.
  5. Wait until `/health/ready` returns 200 from a container running the new image tag.
  6. On failure: automatic `rollback.ps1` to the previous version, including the DB restore from the pre-deploy backup.
- `scripts/deploy/rollback.ps1` is one command:
  - With no arguments it goes back to the previous live version; `-Version` picks a specific one.
  - With `-RestoreDatabase` it stops the app services and runs `restore.ps1 -File <pre-deploy backup of the current
    release> -Force`, which also makes a safety backup and runs CHECKDB.
  - It then **flushes all `sa:*` Redis keys**. Without this, cached DTOs/lists from the pre-restore database would still
    be served; the drill reproduced exactly that.
  - Finally it starts the target images and waits until ready.
- **Schema rollback = restore the pre-deploy backup.** EF down-migrations are not used. **Guard:** the API refuses to
  start (exit code 3, one fatal line) when the database contains applied migrations that this build does not know.
  Integration test: `Api_refuses_to_start_on_a_database_with_newer_migrations`.
- Drill (2026-10-22, on the shared compose stack, git 59b2e03):

  | Step | Result |
  |---|---|
  | deploy 1.0.0 | backup OK, ready, DEPLOYED in 37 s, api container `healthy` |
  | deploy 1.1.0 | backup OK, DEPLOYED in 11–13 s |
  | create stock DRILL2 under 1.1.0 | list count 1, X-Cache HIT |
  | `rollback.ps1 -RestoreDatabase` | 1.1.0 → 1.0.0 in 11 s; restore OK (CHECKDB clean, row counts match the manifest); cache flushed; DRILL2 count 0 (X-Cache MISS); api/ui images :1.0.0 |
  | deploy 1.2.0 with a failing readiness URL | DEPLOY FAILED → automatic rollback to 1.0.0 with DB restore from pre-deploy-1.2.0, exit 1 |
