# StockAccountApp — Status

Status values: `Not started` | `In progress` | `Done` | `Blocked`. Each agent updates only its own rows.
Notes: record deviations from CONTRACT, `REQUEST → <agent>: ...` for cross-area changes, `BUG → <agent>: ...` for defects.

| ID | Task | Owner | Status | Notes |
|---|---|---|---|---|
| PM-1 | CONTRACT.md, PLAN.md, STATUS.md | PM | Done | v1.1 (cookie refresh token, same-origin proxy, Nuxt SSR) |
| DB-1 | Git init, solution, projects, references, build props | DB | Done | `StockAccount.slnx`, Directory.Build.props + Directory.Packages.props (CPM, TreatWarningsAsErrors for src), .gitignore/.gitattributes/.editorconfig, local tool manifest (dotnet-ef 10.0.13). `dotnet build` 0 warnings / 0 errors. Pinned System.Security.Cryptography.Pkcs 10.0.13 (vulnerable transitive from SqlClient). Minimal Api Program.cs + launchSettings (:5080) handed to Backend. |
| DB-2 | Domain entities, enums, domain methods | DB | Done | + `DomainRules` (VAT set, rounding, code regex/normalize), `Invoice.FormatDocumentNo/DefaultSeriesFor/RenumberLines`, `CurrentAccount.IsCompatibleWith`. |
| DB-3 | IApplicationDbContext, ICurrentUser | DB | Done | Deviation: AppUser/AppRole/RefreshToken in `Application/Common/Identity` (Domain must stay package-free). Also `Common/Paging` sort/paging helper (SortMap whitelist) and `QueryFilterNames`. See docs/DB_NOTES.md. |
| DB-4 | AppDbContext, configurations, interceptors, query filters | DB | Done | Named soft-delete filter (no global tenant filter, per §3.8); 26 CHECK constraints, ledger immutability triggers, reconciliation views, collation Turkish_CI_AS. REQUEST → Backend: read DB_NOTES §2 (execution strategy around transactions, IgnoreQueryFilters for history queries, retry on 2601). REQUEST → PM/Backend: ActTrans.Amount for GrandTotal 0 (DB_NOTES D5). |
| DB-5 | AddPersistence DI extension | DB | Done | `AddPersistence(IServiceCollection, IConfiguration)`; Identity not included (Backend adds stores). |
| DB-6 | DataSeeder (idempotent) | DB | Done | Verified twice on SQLEXPRESS: 2 roles, 3 users, 1 company, 5 stocks, 4 accounts, Draft opening invoice (18,714.00). TODO Backend BE-9: approve it at startup. |
| DB-7 | InitialCreate migration applied to SQLEXPRESS | DB | Done | `StockAccountDb`: 16 tables, 21 FKs, 8 filtered unique indexes, 2 views, 2 triggers. Idempotent script command in DB_NOTES §6. |
| BE-1 | Paging/sorting/tenant helpers, exceptions, ProblemDetails, validation | Backend | Done | ProblemDetails always has code/traceId/correlationId; validation keys camelCase; SortMap whitelists. See docs/BACKEND_NOTES.md; BUG-2 fixed (binding errors as problem+json, Turkish) |
| BE-2 | Identity, JWT, refresh tokens, policies, AuthController | Backend | Done | Cookie sa_refresh (HttpOnly, Strict, Path=/), atomic rotation, reuse detection + 30 s grace; refresh/logout need JSON content type (else 415). Admin scope via X-Company-Id header only (sa_company cookie not read by API) |
| BE-3 | Companies & Users | Backend | Done | BUG-1 fixed (companies/current Operator) |
| BE-4 | Cache service + Stocks & CurrentAccounts | Backend | Done | Redis (sa: prefix) with in-memory fallback; versioned list keys; X-Cache verified HIT/MISS |
| BE-5 | Invoices CRUD, approve, cancel, numbering | Backend | Done | Execution strategy + single SaveChanges + retry x3; GrandTotal 0 → no ActTrans (coordinator). Inactive stock/account rejected already on save (422) |
| BE-6 | Transactions, dashboard, portal endpoints | Backend | Done | act-trans sort excludes quantity (no such field) |
| BE-7 | Reconciliation BackgroundService + admin endpoint | Backend | Done | Uses DB views; Reconciliation:InitialDelaySeconds added |
| BE-8 | Serilog, correlation id, CORS, Swagger, ETag, health | Backend | Done | ETag as middleware; Redis failure = Degraded (200), only SQL → 503; Swashbuckle 10.3.1 OK on net10; BUG-3 fixed (2xx schemas, per-op Bearer), BUG-5 fixed (CompanyId on request logs) |
| BE-9 | Startup migrate + seed + approve opening invoice | Backend | Done | Seed opening invoice approved via IInvoiceService → ALS000001, ELMA 100, S001 −18714.00 |
| BE-10 | API Dockerfile, docker-compose.yml, .env.example | Backend | Done | docker compose build api OK; compose smoke (mssql+redis+api) passed Ahmet scenario; UI services not run by Backend |
| BE-11 | README.md | Backend | Done | |
| FE-1 | ui-vue scaffold, layout, types, http client + cache | Frontend | Done | Vite 8 + PrimeVue 4.5 Aura; client cache w/ ETag, LRU, de-dup; 74 Vitest tests; see FRONTEND_NOTES.md |
| FE-2 | Login, auth store, guards, refresh, 403/404, profile | Frontend | Done | in-memory token, single-flight refresh + 500 ms grace retry; X-Company-Id header for admin |
| FE-3 | Dashboard | Frontend | Done | verified vs real API (smoke 16/16) |
| FE-4 | Stocks pages | Frontend | Done | verified vs real API (smoke 16/16) |
| FE-5 | Current accounts pages | Frontend | Done | verified vs real API (smoke 16/16) |
| FE-6 | Invoices pages (form, approve, cancel) | Frontend | Done | verified vs real API (smoke 16/16) |
| FE-7 | Transactions pages | Frontend | Done | verified vs real API (smoke 16/16) |
| FE-8 | Admin pages + company switcher | Frontend | Done | verified vs real API (smoke 16/16) |
| FE-9 | Portal /my-account | Frontend | Done | verified vs real API (smoke 16/16) |
| FE-10 | ui-nuxt port | Frontend | Done | Nuxt 4.6 SSR; SSR refresh + Set-Cookie passthrough, Nitro /api proxy, useAsyncData+getCachedData; uses $fetch (not axios); smoke 16/16 vs real API |
| FE-11 | UI Dockerfiles | Frontend | Done | ui-vue nginx (SPA fallback, /api → api:8080), ui-nuxt node runtime; NOT built: no Docker CLI on this machine |
| TS-1 | Unit tests | Test | Done | Written by Backend (B9); verified 90/90 green. |
| TS-2 | Integration tests | Test | Done | 104 tests (102 pass, 2 skipped = open bugs), per-run StockAccountTests_{guid} on SQLEXPRESS, all 14 groups + concurrency/zero-total. See docs/TEST_REPORT.md. BUG → Backend: BUG-1 (Medium, /companies/current 403 for operator), BUG-2, BUG-3, BUG-5 (Low). |
| TS-3 | Postman collection + newman | Test | Done | 11 folders/113 requests/341 assertions green (local + docker); generated by postman/build-collection.mjs. |
| TS-4 | Playwright E2E (vue + nuxt) | Test | Done | 21 tests per UI, fresh StockAccountDb_E2E + API on 5081 via webServer; all green except BUG-4 (expected failure). BUG → Frontend: BUG-4 (High, ui-nuxt freezes after logout following client-side login). |
| TS-5 | Docker smoke test | Test | Done | compose build OK (all 5 images, first build of UI images), all healthy; newman green; Playwright @smoke 15 pass/1 skip on containers; down -v, sa-redis restarted. |
| TS-6 | Traceability verification | Test | Done | R1–R31 verified (docs/TEST_REPORT.md §7); 5 bugs filed. |
