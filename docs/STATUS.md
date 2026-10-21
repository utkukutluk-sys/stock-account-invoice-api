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
