# StockAccountApp — Project Plan

Binding spec: `docs/CONTRACT.md`. Progress: `docs/STATUS.md` (each agent updates its own rows: status `Not started` → `In progress` → `Done` / `Blocked`, with notes on deviations).
Project root: `C:\Users\PC\source\StockAccountApp`. Environment: Windows, PowerShell, .NET SDK 10.0.402, Node 24, SQL Server Express `.\SQLEXPRESS` (Windows auth), Git 2.56, Docker 29.9 / Compose v5.6.

## 1. Agents and ownership
| Agent | Owns (only this agent writes here) |
|---|---|
| PM | `docs/PLAN.md`, `docs/CONTRACT.md` (STATUS rows: all agents) |
| DB | `StockAccount.slnx`, `Directory.Build.props`, `.gitignore`, `.editorconfig`, project skeletons; `src/StockAccount.Domain/**`; `src/StockAccount.Application/Common/Abstractions/{IApplicationDbContext,ICurrentUser}.cs`; `src/StockAccount.Infrastructure/Persistence/**` (DbContext, configurations, interceptors, migrations, `DataSeeder`) |
| Backend | rest of `src/StockAccount.Application/**`, rest of `src/StockAccount.Infrastructure/**` (Identity/JWT, Caching, BackgroundServices, DependencyInjection), `src/StockAccount.Api/**`, `docker-compose.yml`, `.env.example`, `README.md` |
| Frontend | `ui-vue/**`, `ui-nuxt/**` (incl. their Dockerfiles / nginx.conf) |
| Test | `tests/**`, `e2e/**`, `postman/**` |
If an agent needs a change in another agent's area, it records it in STATUS.md Notes as `REQUEST → <agent>: ...` instead of editing.

## 2. Phases and order
| Phase | Work | Depends on | Parallel with |
|---|---|---|---|
| P0 | PM: contract, plan, status | – | – |
| P1 | DB-1 … DB-7 | P0 | FE-1 … FE-9 (frontend codes against the contract; no API needed to compile) |
| P2 | BE-1 … BE-11 | DB-1 … DB-6 | FE, TS-1 (domain unit tests) |
| P3 | TS-1 … TS-3 (unit, integration, Postman) | BE done | FE-10, FE-11 |
| P4 | FE-10 (Nuxt port) after FE-1 … FE-9; FE-11 Docker; TS-4 E2E (vue then nuxt); TS-5 docker smoke | BE + FE | – |
| P5 | TS-6 traceability verification, bug reports; owners fix; PM final review | all | – |

## 3. Task breakdown

### DB agent
| ID | Task | Definition of done |
|---|---|---|
| DB-1 | `git init`; create solution `StockAccount.slnx`, 4 src + 2 test projects (net10.0, xUnit for tests), project references (CONTRACT §1), `Directory.Build.props`, `.gitignore` (VS/.NET/Node/logs/.env), `.editorconfig`. Test projects are empty shells owned afterwards by Test agent. | `dotnet build` succeeds |
| DB-2 | Domain: base types, enums, entities, `DomainException`, domain methods (CONTRACT §2.1–2.4) | compiles; no package deps in Domain |
| DB-3 | `IApplicationDbContext` (DbSets for all entities + `Users`, `RefreshTokens`, `SaveChangesAsync`, `Database` facade for transactions) and `ICurrentUser` (CONTRACT §4.3) in Application | compiles |
| DB-4 | `AppDbContext : IdentityDbContext<AppUser, AppRole, int>, IApplicationDbContext`; Fluent configurations (lengths, precision, indexes incl. filtered unique, delete behaviors, rowversion), soft-delete query filters, `AuditSaveChangesInterceptor` (audit + soft delete) | matches CONTRACT §2 exactly |
| DB-5 | `AddPersistence(IServiceCollection, IConfiguration)` extension (SQL Server, `EnableRetryOnFailure`, interceptor registration, Identity stores not included — Backend adds Identity on top via `AddEntityFrameworkStores<AppDbContext>()`) | Backend can call it |
| DB-6 | `DataSeeder.SeedAsync(IServiceProvider)` per CONTRACT §9 (roles, users via `UserManager`, company, stocks, accounts, Draft opening purchase invoice with computed totals), idempotent | running twice creates no duplicates |
| DB-7 | Migration `InitialCreate` (`dotnet ef migrations add InitialCreate -p src/StockAccount.Infrastructure -s src/StockAccount.Api -o Persistence/Migrations`) and apply to `.\SQLEXPRESS` `StockAccountDb`; requires minimal Api `Program.cs` + design-time factory `AppDbContextFactory` (Infrastructure) | DB exists with all tables/indexes; build green |

### Backend agent
| ID | Task | Definition of done |
|---|---|---|
| BE-1 | Common: `PagedRequest`/`PagedResult<T>`, sort parser with per-entity whitelist, `ForTenant` extension, exception types, `GlobalExceptionHandler` → ProblemDetails with `code`, `traceId`, `correlationId`, validation `errors` (CONTRACT §5.1–5.2); FluentValidation registration + automatic validation (endpoint filter or explicit `ValidateAndThrowAsync` in services) | uniform error bodies |
| BE-2 | Auth: Identity config, JWT issuing/validation, refresh tokens in the httpOnly `sa_refresh` cookie with rotation, reuse detection and a 30 s grace window (no refresh token in bodies), `UseForwardedHeaders`, `CurrentUser` implementation (incl. `X-Company-Id` for admin), policies `AdminOnly/Operator/Portal`, `AuthController` (CONTRACT §4, §5.4) | login/refresh/logout/me/change-password work |
| BE-3 | Companies & Users services + controllers (§5.5, §5.6) | CRUD, rules, 409 codes |
| BE-4 | `ICacheService` (Redis with in-memory fallback, versioned list keys, `X-Cache` header) + Stocks & CurrentAccounts services/controllers incl. transactions sub-resources (§5.7, §5.8, §6) | cached reads, invalidation on CUD |
| BE-5 | Invoices: CRUD (Draft only), approve/cancel with transaction, numbering, negative-stock check, concurrency retry, ledger rows, balance update, cache invalidation (§3, §5.9) | Ahmet/Apple scenario passes manually via Swagger |
| BE-6 | `/stock-trans`, `/act-trans`, `/dashboard/summary`, `/me/*` portal endpoints (§5.10) | |
| BE-7 | `BalanceReconciliationService` (BackgroundService) + `IReconciliationService` + `/admin/reconciliation/run` (§3.9) | discrepancy logged at Warning |
| BE-8 | Program.cs: Serilog (bootstrap, console, rolling file, request logging, enrichers), `CorrelationIdMiddleware`, CORS, Swagger with JWT + XML comments + enums as strings, ETag filter (§5.3), health checks `/health`, `JsonStringEnumConverter`, `public partial class Program {}` | `/swagger` and `/health` work; logs in `logs/` |
| BE-9 | Startup: migrate (with retry) + `DataSeeder` + approve seed opening invoice (§9) when config flags set | fresh DB fully seeded on first run, ELMA balance 100 |
| BE-10 | `src/StockAccount.Api/Dockerfile` (multi-stage, sdk:10.0 → aspnet:10.0, port 8080), `docker-compose.yml`, `.env.example` (§8.3) | `docker compose up --build` starts all 5 services |
| BE-11 | Root `README.md`: prerequisites, local run (API, ui-vue, ui-nuxt, Redis optional), docker run, seed credentials, Swagger/Postman/test commands, architecture summary & key decisions | a newcomer can run the app |

### Frontend agent
| ID | Task | Definition of done |
|---|---|---|
| FE-1 | Scaffold `ui-vue` (Vite vue-ts), Vite dev proxy `/api` → 5080, PrimeVue Aura + Turkish locale, PrimeFlex, primeicons, Pinia, router, layout (sidebar/topbar, responsive), TS DTO types, label/format utils, `http.ts` (base `/api`, credentials) with auth interceptors + client cache (§8.1, §10.1–10.3, §10.5) | `npm run build` and `vue-tsc` clean |
| FE-2 | Login page, in-memory auth store (no web storage), startup silent refresh via cookie, guards, single-flight refresh, logout, `/403`, 404, `/profile`; list filters synced to URL query | |
| FE-3 | Dashboard | |
| FE-4 | Stocks list + form + transactions tab | |
| FE-5 | Current accounts list + form + transactions tab | |
| FE-6 | Invoices list + form (lines, live totals, AutoCompletes, approve, cancel dialog, delete) | Ahmet/Apple flow works in UI |
| FE-7 | Stock / account transactions pages | |
| FE-8 | Admin: companies, users, reconciliation, company switcher | |
| FE-9 | Portal `/my-account` | |
| FE-10 | `ui-nuxt`: Nuxt 4 port with **`ssr: true`**: `/api` proxy server route, SSR refresh with cookie forwarding + `Set-Cookie` pass-through, `useState` auth, `useAsyncData` + `getCachedData` data loading, rendering modes per CONTRACT §10.7, identical routes, texts and test ids | E2E suite (incl. #11 SSR) passes on both UIs |
| FE-11 | `ui-vue/Dockerfile` (node build → nginx, SPA fallback `try_files ... /index.html`, `location /api/` proxy to `http://api:8080` with forwarded headers), `ui-nuxt/Dockerfile` (node build → node runtime, port 3000, env `NUXT_API_BASE`) | images build; both UIs work through compose |

### Test agent
| ID | Task | Definition of done |
|---|---|---|
| TS-1 | Unit tests (CONTRACT §11.1) | `dotnet test tests/StockAccount.UnitTests` green |
| TS-2 | Integration tests (§11.2), all 14 scenario groups | green against SQLEXPRESS |
| TS-3 | Postman collection + 2 environments + newman script (§11.3) | `npm test` in `postman/` green against running API |
| TS-4 | Playwright E2E (§11.4) for ui-vue and ui-nuxt | both `test:vue` and `test:nuxt` green |
| TS-5 | Docker smoke: `docker compose up --build -d`, wait for `/health`, run newman with docker env, open both UIs (Playwright against docker ports) | documented result in STATUS |
| TS-6 | Walk the traceability checklist (§6) and mark each line verified; file defects as STATUS notes `BUG → <agent>: ...` | all lines verified |

## 4. Definition of done (global)
- `dotnet build` (0 errors) and `dotnet test` green; `npm run build` green for ui-vue and ui-nuxt; E2E green on both UIs; newman green.
- API behavior, DTO shapes, routes, status codes and error codes exactly as CONTRACT; Swagger shows all endpoints with auth.
- No secrets other than documented dev defaults; `.env` git-ignored.
- Logs written to console and `logs/api-*.log`, with correlation id.
- Every row in STATUS.md `Done`, deviations noted; traceability checklist fully checked.

## 5. Open risks
| Risk | Mitigation |
|---|---|
| Refresh-token rotation races: parallel tabs, or SSR and client refreshing at the same time | 30 s grace window without family revocation, plus a single client retry (CONTRACT §4.1, §10.2); covered by TS-2 #1 |
| SameSite=Strict cookie is not sent on cross-site first navigation, so Nuxt SSR renders that request as anonymous | Client bootstrap refresh recovers the session (§10.7); this is accepted as a cosmetic redirect delay |
| Refresh cookie uses `Path=/` instead of `/api/auth`, so it is also sent to page requests on the UI origin | Required for SSR; the cookie stays HttpOnly and SameSite=Strict and is accepted only by `/auth/refresh` and `/auth/logout` |
| Package compatibility with .NET 10: Swashbuckle, EF tools and health-check packages | Use latest stable versions. If a package fails, record the deviation in STATUS. Fallback: Microsoft.AspNetCore.OpenApi + Swagger UI |
| SQL Server container memory (≥ 2 GB) on the dev machine; Redis not running locally | Local dev uses SQLEXPRESS; the in-memory cache fallback is acceptable for dev |
| Agents cannot talk to each other | Cross-area changes go through `REQUEST →` / `BUG →` notes in STATUS, triaged by the coordinator |

## 6. Requirement traceability checklist
| # | Assignment requirement | Satisfied by | Verified by |
|---|---|---|---|
| R1 | Web API providing Stock services | BE-4 (§5.7) | TS-2 #4, TS-3 |
| R2 | Web API providing Current Account services | BE-4 (§5.8) | TS-2 #4, TS-3 |
| R3 | Web API providing Invoice/Receipt (Fiş) services | BE-5 (§5.9) | TS-2 #9–11 |
| R4 | Admin / User authorization | BE-2, BE-3, policies §4.2; FE-2 guards | TS-2 #1–3, TS-4 #6–7 |
| R5 | CRUD for all entities | Company, User, Stock, CurrentAccount, Invoice(+lines) full CRUD (BE-3/4/5). StockTrans/ActTrans are immutable ledgers: read-only by design, created/reversed only via approve/cancel (documented decision) | TS-2 #4 |
| R6 | Lists with paging and filtering | §5.1 on every list (BE-1…6); FE lazy DataTables | TS-2 #5, TS-4 #8 |
| R7 | Stock & Current Account cached in Redis | BE-4, §6 | TS-2 #7 |
| R8 | Cache auto-refresh on CRUD (and balance changes) | §6 invalidation table (BE-4, BE-5) | TS-2 #7 |
| R9 | User-friendly, optimized UI/UX | FE-1…9 (PrimeVue Aura, responsive, lazy tables, debounced filters, toasts, confirmations, inline validation, live totals) | TS-4 |
| R10 | UI caches HTTP requests | §10.3 client cache + ETag/304 (BE-8); Nuxt also caches through the `useAsyncData` payload and `getCachedData` (§10.7) | TS-4 #10, TS-2 #8 |
| R11 | Admin controls base entities | Companies/Users AdminOnly; delete stock/account and cancel invoice AdminOnly (§4.2, §5) | TS-2 #2 |
| R12 | Company = main entity, all assets belong to it | CompanyId on all tenant entities + isolation §3.8 (DB-2/4, BE-1) | TS-2 #3 |
| R13 | Company can be a user | Users belong to a Company and act for it (Operator) §4.2 | TS-2 #3 |
| R14 | Stock used in invoice lines | InvoiceLine.StockId (DB-2) | TS-2 #9 |
| R15 | Current Account = invoice counterparty; can be a user | Invoice.CurrentAccountId; portal user linked to CurrentAccount (`/me/*`, FE-9) | TS-2 #2, TS-4 #6 |
| R16 | Invoice header (account, date, series) + lines | §2.3 Invoice/InvoiceLine | TS-2 #4, #9 |
| R17 | Invoice created as Draft | §3.3 | TS-2 #9 |
| R18 | Approval creates account & stock movements | BE-5, §3.2 | TS-2 #9, #11, TS-4 #4 |
| R19 | Ahmet / 2 kg Apple example | Seed ELMA & C001 (DB-6); §3.2 reference scenario | TS-2 #9, TS-3 scenario folder, TS-4 #4 |
| R20 | Balances (sum of trans grouped by stock/account) stored on Stock/CurrentAccount | Stock.Balance / CurrentAccount.Balance updated in approve/cancel; reconciliation BackgroundService (BE-7) | TS-2 #9, #13 |
| R21 | .NET (8 → we use 10) | DB-1 (net10.0) | build |
| R22 | MSSQL | DB-4/5/7, SQLEXPRESS + docker mssql | TS-2 |
| R23 | Redis | BE-4, compose redis | TS-2 #7, TS-5 |
| R24 | Serilog | BE-8 | manual: log file, TS-2 #14 |
| R25 | PrimeVue + PrimeFlex | FE-1 | review |
| R26 | Postman testing | TS-3 | newman run |
| R27 | Swagger | BE-8 | TS-6 manual |
| R28 | Vue.js project | FE-1…9 (`ui-vue`) | TS-4 vue |
| R29 | (Optional→required) migrate UI to Nuxt | FE-10 (`ui-nuxt`, SSR) | TS-4 nuxt incl. #11 |
| R30 | (Optional→required) Dockerize | BE-10, FE-11 | TS-5 |
| R31 | Extra decided: Clean Architecture, EF migrations, JWT refresh, modular monolith + BackgroundService, ProblemDetails, FluentValidation, seed data, unit/integration/E2E tests | DB-*, BE-*, TS-* | TS-6 |
