# StockAccountApp — Status

Status values: `Not started` | `In progress` | `Done` | `Blocked`. Each agent updates only its own rows.
Notes: record deviations from CONTRACT, `REQUEST → <agent>: ...` for cross-area changes, `BUG → <agent>: ...` for defects.

| ID | Task | Owner | Status | Notes |
|---|---|---|---|---|
| PM-1 | CONTRACT.md, PLAN.md, STATUS.md | PM | Done | v1.0 |
| DB-1 | Git init, solution, projects, references, build props | DB | Not started | |
| DB-2 | Domain entities, enums, domain methods | DB | Not started | |
| DB-3 | IApplicationDbContext, ICurrentUser | DB | Not started | |
| DB-4 | AppDbContext, configurations, interceptors, query filters | DB | Not started | |
| DB-5 | AddPersistence DI extension | DB | Not started | |
| DB-6 | DataSeeder (idempotent) | DB | Not started | |
| DB-7 | InitialCreate migration applied to SQLEXPRESS | DB | Not started | |
| BE-1 | Paging/sorting/tenant helpers, exceptions, ProblemDetails, validation | Backend | Not started | |
| BE-2 | Identity, JWT, refresh tokens, policies, AuthController | Backend | Not started | |
| BE-3 | Companies & Users | Backend | Not started | |
| BE-4 | Cache service + Stocks & CurrentAccounts | Backend | Not started | |
| BE-5 | Invoices CRUD, approve, cancel, numbering | Backend | Not started | |
| BE-6 | Transactions, dashboard, portal endpoints | Backend | Not started | |
| BE-7 | Reconciliation BackgroundService + admin endpoint | Backend | Not started | |
| BE-8 | Serilog, correlation id, CORS, Swagger, ETag, health | Backend | Not started | |
| BE-9 | Startup migrate + seed + approve opening invoice | Backend | Not started | |
| BE-10 | API Dockerfile, docker-compose.yml, .env.example | Backend | Not started | |
| BE-11 | README.md | Backend | Not started | |
| FE-1 | ui-vue scaffold, layout, types, http client + cache | Frontend | Not started | |
| FE-2 | Login, auth store, guards, refresh, 403/404, profile | Frontend | Not started | |
| FE-3 | Dashboard | Frontend | Not started | |
| FE-4 | Stocks pages | Frontend | Not started | |
| FE-5 | Current accounts pages | Frontend | Not started | |
| FE-6 | Invoices pages (form, approve, cancel) | Frontend | Not started | |
| FE-7 | Transactions pages | Frontend | Not started | |
| FE-8 | Admin pages + company switcher | Frontend | Not started | |
| FE-9 | Portal /my-account | Frontend | Not started | |
| FE-10 | ui-nuxt port | Frontend | Not started | |
| FE-11 | UI Dockerfiles | Frontend | Not started | |
| TS-1 | Unit tests | Test | Not started | |
| TS-2 | Integration tests | Test | Not started | |
| TS-3 | Postman collection + newman | Test | Not started | |
| TS-4 | Playwright E2E (vue + nuxt) | Test | Not started | |
| TS-5 | Docker smoke test | Test | Not started | |
| TS-6 | Traceability verification | Test | Not started | |
