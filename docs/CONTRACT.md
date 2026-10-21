# StockAccountApp — Binding Contract (v1.0)

This document is the **single source of truth** for all agents (Database, Backend, Frontend, Test).
If code and contract disagree, the contract wins. Agents must not change this file; if something is
impossible as written, implement the closest compliant option and record the deviation in `docs/STATUS.md` (Notes column).

Conventions used below: "MUST" = mandatory. `T?` = nullable. Lengths are `nvarchar(n)` unless stated.
All money amounts are `decimal(18,2)`, prices `decimal(18,4)`, quantities `decimal(18,3)`, rates `decimal(5,2)`.
Rounding everywhere: `Math.Round(x, 2, MidpointRounding.AwayFromZero)`.

---

## 1. Solution layout

```
C:\Users\PC\source\StockAccountApp\
├─ StockAccount.slnx                      (.NET 10 solution, created with `dotnet new sln`)
├─ Directory.Build.props                  (net10.0, Nullable=enable, ImplicitUsings=enable, LangVersion=latest)
├─ .gitignore  .editorconfig  .env.example  docker-compose.yml  README.md
├─ docs\  (CONTRACT.md, PLAN.md, STATUS.md)
├─ src\
│  ├─ StockAccount.Domain\           entities, enums, domain exceptions, domain calc methods (no package deps)
│  ├─ StockAccount.Application\      DTOs, validators, service interfaces + implementations, abstractions
│  ├─ StockAccount.Infrastructure\   EF Core DbContext/configs/migrations/seed, Identity, JWT, caching, background service
│  └─ StockAccount.Api\              controllers, middleware, Program.cs, Swagger, Serilog, Dockerfile
├─ tests\
│  ├─ StockAccount.UnitTests\        xUnit
│  └─ StockAccount.IntegrationTests\ xUnit + WebApplicationFactory
├─ ui-vue\                           Vue 3 + Vite + TS + PrimeVue 4 + PrimeFlex 4 (+ Dockerfile, nginx.conf)
├─ ui-nuxt\                          Nuxt 4 port of ui-vue (+ Dockerfile)
├─ e2e\                              Playwright (TypeScript), runs against either UI via E2E_BASE_URL
└─ postman\                          StockAccount.postman_collection.json, local/docker environments
```

Project references: Application → Domain; Infrastructure → Application, Domain; Api → Application, Infrastructure;
UnitTests → Domain, Application, Infrastructure; IntegrationTests → Api.

Namespaces = project name + folder path. Application folders:
`Common/{Abstractions,Exceptions,Paging,Caching,Security}`, `Features/{Auth,Companies,Users,Stocks,CurrentAccounts,Invoices,Transactions,Dashboard,Portal,Reconciliation}` each with `Dtos/`, `Validators/`, `I<Name>Service.cs`, `<Name>Service.cs`.

### 1.1 Key packages (use latest stable compatible with net10.0)
| Project | Packages |
|---|---|
| Application | Microsoft.EntityFrameworkCore (abstractions for IQueryable/DbSet), FluentValidation, FluentValidation.DependencyInjectionExtensions, Microsoft.Extensions.Caching.Abstractions |
| Infrastructure | Microsoft.EntityFrameworkCore.SqlServer, Microsoft.EntityFrameworkCore.Design, Microsoft.AspNetCore.Identity.EntityFrameworkCore, Microsoft.Extensions.Caching.StackExchangeRedis, System.IdentityModel.Tokens.Jwt / Microsoft.IdentityModel.JsonWebTokens |
| Api | Microsoft.AspNetCore.Authentication.JwtBearer, Swashbuckle.AspNetCore, Serilog.AspNetCore, Serilog.Sinks.Console, Serilog.Sinks.File, Serilog.Enrichers.Environment, Serilog.Enrichers.Thread, AspNetCore.HealthChecks.SqlServer, AspNetCore.HealthChecks.Redis, Microsoft.EntityFrameworkCore.Tools |
| UnitTests | xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, NSubstitute, Microsoft.EntityFrameworkCore.InMemory (or Sqlite) , coverlet.collector |
| IntegrationTests | xunit, Microsoft.AspNetCore.Mvc.Testing, Microsoft.NET.Test.Sdk, coverlet.collector |

**Not allowed** (license reasons): MediatR ≥13, AutoMapper ≥15, FluentAssertions ≥8. Use plain service classes, manual mapping (static `ToDto()` extension methods), and xUnit `Assert`.
Global tool: `dotnet-ef` (install with `dotnet tool install --global dotnet-ef` if missing).

npm (ui-vue): vue@3, vue-router@4, pinia, axios, primevue@4, @primeuix/themes, primeflex@4, primeicons, typescript, vite, @vitejs/plugin-vue, vue-tsc.
npm (ui-nuxt): nuxt@4, @primevue/nuxt-module, primevue@4, @primeuix/themes, primeflex@4, primeicons, @pinia/nuxt, pinia, axios.
npm (e2e): @playwright/test. npm (postman runner): newman (devDependency in `postman/package.json`).

---

## 2. Domain model

### 2.1 Base types (Domain/Common)
- `AuditableEntity`: `int Id` (identity PK), `DateTime CreatedAt` (UTC, datetime2, required), `int? CreatedByUserId`, `DateTime? UpdatedAt`, `int? UpdatedByUserId`.
- `ISoftDelete`: `bool IsDeleted` (default false), `DateTime? DeletedAt`, `int? DeletedByUserId`.
- `ITenantEntity`: `int CompanyId`.
- `byte[] RowVersion` with `[Timestamp]`/`IsRowVersion()` on Company, Stock, CurrentAccount, Invoice.
- Audit fields are set by an EF `SaveChangesInterceptor` (Infrastructure) using `ICurrentUser.UserId` (null when no user: seeding/background).
- Soft delete: interceptor converts `EntityState.Deleted` of `ISoftDelete` entities into update with IsDeleted=true. Global query filter `!IsDeleted` on soft-delete entities.
- User audit columns are plain ints (no FK).

### 2.2 Enums (stored as `int`, serialized in JSON as **strings** via `JsonStringEnumConverter`)
| Enum | Values |
|---|---|
| `StockUnit` | Piece=1, Kg=2, Gram=3, Liter=4, Meter=5, Box=6 |
| `CurrentAccountType` | Customer=1, Supplier=2, Both=3 |
| `InvoiceType` | Sales=1, Purchase=2 |
| `InvoiceStatus` | Draft=1, Approved=2, Cancelled=3 |
| `StockTransDirection` | In=1, Out=2 |
| `ActTransDirection` | Debit=1, Credit=2 |

### 2.3 Entities

**Company** (table `Companies`) — AuditableEntity, ISoftDelete, RowVersion
| Field | Type | Rules |
|---|---|---|
| Name | nvarchar(200) | required |
| TaxNumber | varchar(11) | required, 10–11 digits; unique filtered `WHERE IsDeleted = 0` |
| TaxOffice | nvarchar(100)? | |
| Phone | varchar(20)? | |
| Email | nvarchar(256)? | email format |
| Address | nvarchar(500)? | |
| City | nvarchar(100)? | |
| IsActive | bit | default true |

**AppUser** (Identity, table `AspNetUsers`, `IdentityUser<int>`; `AppRole : IdentityRole<int>`; context `IdentityDbContext<AppUser, AppRole, int>`)
| Field | Type | Rules |
|---|---|---|
| Email / UserName | (Identity) | UserName = Email, unique |
| FullName | nvarchar(150) | required |
| CompanyId | int? FK→Companies (Restrict) | required when role=User; must be null when role=Admin |
| CurrentAccountId | int? FK→CurrentAccounts (Restrict) | only for role=User; account must belong to same company; unique filtered `WHERE CurrentAccountId IS NOT NULL` |
| IsActive | bit | default true; inactive users cannot log in or refresh |
| CreatedAt | datetime2 | UTC |
| LastLoginAt | datetime2? | |
Each user has exactly one role: `Admin` or `User`. Users are hard-deleted (refresh tokens cascade).

**RefreshToken** (table `RefreshTokens`)
| Field | Type | Rules |
|---|---|---|
| Id | int identity | |
| UserId | int FK→AspNetUsers (Cascade) | |
| TokenHash | varchar(64) | SHA-256 hex of the raw token; unique |
| ExpiresAt, CreatedAt | datetime2 | UTC |
| CreatedByIp | varchar(45)? | |
| RevokedAt | datetime2? | |
| ReplacedByTokenHash | varchar(64)? | |
| RevokedReason | nvarchar(100)? | |

**Stock** (table `Stocks`) — AuditableEntity, ISoftDelete, ITenantEntity, RowVersion
| Field | Type | Rules |
|---|---|---|
| CompanyId | int FK→Companies (Restrict) | |
| Code | nvarchar(50) | required, uppercase-normalized (trim + ToUpperInvariant), regex `^[A-Z0-9._-]{1,50}$`; unique (CompanyId, Code) filtered `IsDeleted = 0` |
| Name | nvarchar(200) | required |
| Unit | int (StockUnit) | required |
| Barcode | varchar(50)? | |
| VatRate | decimal(5,2) | one of 0, 1, 10, 20; default 20 |
| UnitPrice | decimal(18,4) | default sales price, ≥ 0 |
| Description | nvarchar(500)? | |
| IsActive | bit | default true |
| Balance | decimal(18,3) | **system-maintained only** (never accepted from API input), default 0 |
Index: (CompanyId, Name).

**CurrentAccount** (table `CurrentAccounts`) — AuditableEntity, ISoftDelete, ITenantEntity, RowVersion
| Field | Type | Rules |
|---|---|---|
| CompanyId | int FK→Companies (Restrict) | |
| Code | nvarchar(50) | same normalization/regex/unique rule as Stock.Code |
| Title | nvarchar(200) | required (Unvan / Ad Soyad) |
| Type | int (CurrentAccountType) | required |
| TaxNumber | varchar(11)? | 10–11 digits if present |
| TaxOffice | nvarchar(100)? | |
| Phone | varchar(20)? | |
| Email | nvarchar(256)? | |
| Address | nvarchar(500)? | |
| City | nvarchar(100)? | |
| IsActive | bit | default true |
| Balance | decimal(18,2) | system-maintained only, default 0 |
Index: (CompanyId, Title).

**Invoice** (table `Invoices`) — AuditableEntity, ITenantEntity, RowVersion. **Not** soft-deleted (Draft invoices are hard-deleted).
| Field | Type | Rules |
|---|---|---|
| CompanyId | int FK→Companies (Restrict) | |
| Type | int (InvoiceType) | required, immutable after creation |
| Status | int (InvoiceStatus) | Draft on create |
| Series | varchar(10) | required, regex `^[A-Z0-9]{1,10}$` (normalized upper); default "SAT" for Sales, "ALS" for Purchase when omitted |
| Number | int? | null while Draft; assigned on approval |
| DocumentNo | varchar(20)? | `Series + Number.ToString("D6")`, set on approval (e.g. `SAT000001`) |
| InvoiceDate | date (DateOnly) | required |
| DueDate | date? | ≥ InvoiceDate |
| CurrentAccountId | int FK→CurrentAccounts (Restrict) | required, same company |
| Description | nvarchar(500)? | |
| SubTotal | decimal(18,2) | computed |
| VatTotal | decimal(18,2) | computed |
| GrandTotal | decimal(18,2) | computed |
| ApprovedAt | datetime2? / ApprovedByUserId int? | |
| CancelledAt | datetime2? / CancelledByUserId int? | |
| CancelReason | nvarchar(500)? | |
Indexes: unique (CompanyId, Series, Number) filtered `WHERE Number IS NOT NULL`; (CompanyId, InvoiceDate); (CurrentAccountId); (Status).

**InvoiceLine** (table `InvoiceLines`)
| Field | Type | Rules |
|---|---|---|
| Id | int identity | |
| InvoiceId | int FK→Invoices (**Cascade**) | |
| LineNo | int | 1..n, in request order |
| StockId | int FK→Stocks (Restrict) | same company, active |
| Description | nvarchar(250)? | |
| Quantity | decimal(18,3) | > 0 |
| UnitPrice | decimal(18,4) | ≥ 0 |
| VatRate | decimal(5,2) | one of 0, 1, 10, 20 |
| LineNet | decimal(18,2) | = round(Quantity × UnitPrice, 2) |
| VatAmount | decimal(18,2) | = round(LineNet × VatRate / 100, 2) |
| LineTotal | decimal(18,2) | = LineNet + VatAmount |
Unique (InvoiceId, LineNo). Max 200 lines per invoice.

**StockTrans** (table `StockTrans`) — immutable ledger, no update/delete
| Field | Type | Rules |
|---|---|---|
| Id | bigint identity | |
| CompanyId | int FK→Companies (Restrict) | |
| StockId | int FK→Stocks (Restrict) | |
| InvoiceId | int FK→Invoices (Restrict) | |
| InvoiceLineId | int? (no FK, informational) | |
| TransDate | date | |
| Direction | int (StockTransDirection) | |
| Quantity | decimal(18,3) | always > 0 |
| UnitPrice | decimal(18,4) | |
| Amount | decimal(18,2) | = line LineNet |
| IsReversal | bit | true for cancellation rows |
| Description | nvarchar(250) | e.g. `SAT000001 - Satış faturası` / `SAT000001 - İptal` |
| CreatedAt | datetime2 / CreatedByUserId int? | |
Indexes: (StockId, TransDate), (InvoiceId).

**ActTrans** (table `ActTrans`) — immutable ledger
| Field | Type | Rules |
|---|---|---|
| Id | bigint identity | |
| CompanyId | int FK→Companies (Restrict) | |
| CurrentAccountId | int FK→CurrentAccounts (Restrict) | |
| InvoiceId | int FK→Invoices (Restrict) | |
| TransDate | date | |
| Direction | int (ActTransDirection) | |
| Amount | decimal(18,2) | always > 0 (= invoice GrandTotal) |
| IsReversal | bit | |
| Description | nvarchar(250) | |
| CreatedAt | datetime2 / CreatedByUserId int? | |
Indexes: (CurrentAccountId, TransDate), (InvoiceId).

### 2.4 Domain methods (in Domain, owned by DB agent, used by Backend)
- `InvoiceLine.Calculate()` — computes LineNet, VatAmount, LineTotal per formulas above.
- `Invoice.RecalculateTotals()` — calls Calculate on each line; SubTotal = Σ LineNet, VatTotal = Σ VatAmount, GrandTotal = SubTotal + VatTotal.
- `Invoice.EnsureDraft()` — throws `DomainException("INVOICE_NOT_DRAFT")` if not Draft.
- `DomainException(string code, string message)` in Domain/Common.

---

## 3. Business rules

### 3.1 Balance sign conventions
- `Stock.Balance` = Σ Quantity(In) − Σ Quantity(Out) over StockTrans for that stock (reversal rows included as normal rows).
- `CurrentAccount.Balance` = Σ Amount(Debit) − Σ Amount(Credit) over ActTrans. **Positive = the account owes us (receivable, "Borçlu")**, negative = we owe them (payable, "Alacaklı").

### 3.2 Invoice effects on approval
| Type | StockTrans per line | ActTrans (one row) |
|---|---|---|
| Sales | Out, Quantity = line qty | Debit, Amount = GrandTotal |
| Purchase | In, Quantity = line qty | Credit, Amount = GrandTotal |
Cancellation of an Approved invoice writes the exact opposite rows (`IsReversal = true`, TransDate = cancel date (UTC today)), and reverses balances. Original rows are never modified.

**Reference scenario (Ahmet/Apple):** Stock `ELMA` (Kg, VatRate 1), account `C001 Ahmet Yılmaz`. Sales invoice: 2 kg ELMA × 50.00, VAT 1% → LineNet 100.00, VAT 1.00, GrandTotal 101.00. On approval: StockTrans {ELMA, Out, 2.000}, ActTrans {Ahmet, Debit, 101.00}; ELMA.Balance −= 2; Ahmet.Balance += 101.00.

### 3.3 Status machine
- Create → Draft. Only Draft can be updated (PUT) or deleted (hard delete). Otherwise 409 `INVOICE_NOT_DRAFT`.
- Approve: Draft → Approved. Requires ≥1 line (422 `INVOICE_HAS_NO_LINES`), active non-deleted account and stocks (422 `INACTIVE_ACCOUNT` / `INACTIVE_STOCK`), account type compatible: Sales needs Customer|Both, Purchase needs Supplier|Both (422 `ACCOUNT_TYPE_MISMATCH`; also validated on create/update).
- Cancel: Approved → Cancelled only (409 `INVOICE_NOT_APPROVED` otherwise). Reason required (3–500 chars). Draft invoices are deleted, not cancelled.
- Totals are always recomputed server-side; clients never send totals.

### 3.4 Numbering
On approval: `Number = (MAX(Number) WHERE CompanyId = x AND Series = s) + 1` (1 if none); `DocumentNo = Series + Number:D6`. Series sequences are per company, shared by both types. Gaps cannot occur because numbers are only given at approval. On unique-index violation retry (see 3.6).

### 3.5 Negative stock policy
Config `Inventory:AllowNegativeStock` (default **false**). When false, approving a Sales invoice (or cancelling an Approved Purchase invoice) fails with 422 `INSUFFICIENT_STOCK` if for any stock `Balance − Σ(quantities of that stock in the invoice) < 0`. The ProblemDetails `detail` lists offending stock codes. Current accounts may go negative freely.

### 3.6 Transaction & concurrency
Approve and Cancel run in one DB transaction (`BeginTransactionAsync`, ReadCommitted): load invoice+lines, verify the request `rowVersion` equals the invoice's (else 409 `CONCURRENCY_CONFLICT`), load affected Stocks and CurrentAccount (tracked), check rules, assign number (approve), add ledger rows, update balances, update status/approval fields, `SaveChanges`, commit. Optimistic concurrency via RowVersion on Stock/CurrentAccount/Invoice: on `DbUpdateConcurrencyException` or unique-number violation, roll back, clear the change tracker, and retry the whole operation up to **3** times. The client-supplied invoice rowVersion is checked only on the first attempt; retries reload fresh Invoice/Stock/Account state and re-check status and all rules. After 3 failures → 409 `CONCURRENCY_CONFLICT`. Cache invalidation happens only after commit.

### 3.7 Delete rules (soft delete for Company/Stock/CurrentAccount)
- Stock: 409 `STOCK_HAS_BALANCE` if Balance ≠ 0; 409 `STOCK_IN_USE` if referenced by any Draft invoice line.
- CurrentAccount: 409 `ACCOUNT_HAS_BALANCE` if Balance ≠ 0; 409 `ACCOUNT_IN_USE` if referenced by a Draft invoice or linked to a user.
- Company: 409 `COMPANY_NOT_EMPTY` if it has any non-deleted stock, current account, invoice or user.
- Soft-deleted rows are invisible to all APIs; their Code may be reused.
- Invoice (Draft only): hard delete, lines cascade. StockTrans/ActTrans: never deleted.

### 3.8 Multi-tenancy
- Every tenant entity carries CompanyId. Non-admin users only ever see/modify rows of their `company_id` claim; requests for another company's row return **404** (not 403).
- Admin: optional header `X-Company-Id: <int>`. If present, reads are filtered to that company and creates use it. If absent, reads return all companies; creates of tenant entities return 400 `COMPANY_REQUIRED`. Non-admins: header ignored.
- Cross-entity checks: invoice's account and stocks must belong to the invoice's company (else 422 `INVALID_REFERENCE`).
- Tenant filtering is explicit in Application queries via an `IQueryable<T> ForTenant(ICurrentUser)` extension (no global tenant filter).

### 3.9 Reconciliation background service
`BalanceReconciliationService : BackgroundService` (Infrastructure). Every `Reconciliation:IntervalMinutes` (default 5; first run 30 s after start) it computes per stock Σ ledger vs `Stock.Balance` and per account Σ ledger vs `CurrentAccount.Balance` (all companies, ignoring soft-delete filter). Each mismatch logged at **Warning** with `{EntityType} {EntityId} {Code} {StoredBalance} {LedgerBalance} {Difference}`; a summary at Information. If `Reconciliation:AutoFix` = true (default false) it sets stored = ledger and logs. Exposed for on-demand use through `IReconciliationService.RunAsync(bool fix)` (endpoint 5.10). Exceptions are logged, never crash the host.

---

## 4. Authentication & authorization

### 4.1 Tokens
- JWT HS256. `Jwt:Issuer`=`StockAccount`, `Jwt:Audience`=`StockAccount.Clients`, `Jwt:Key` ≥ 32 chars (config/env). ClockSkew 30 s.
- Access token lifetime **15 min** (`Jwt:AccessTokenMinutes`), refresh token **7 days** (`Jwt:RefreshTokenDays`).
- Refresh token = 64 random bytes, Base64Url; only SHA-256 hash stored. **Rotation**: each refresh revokes the used token (`ReplacedByTokenHash`) and issues a new one. Presenting a revoked token → revoke all active tokens of that user (reason "Reuse detected") and return 401 `INVALID_REFRESH_TOKEN`.
- Claims: `sub` (user id), `email`, `name` (FullName), `role` (`Admin`|`User`; map so `[Authorize(Roles=..)]` works — set `RoleClaimType = "role"`, `NameClaimType = "name"`, `MapInboundClaims = false`), `company_id` (if any), `account_id` (if linked), `jti`.
- Identity options: password min 8, requires upper, lower, digit; non-alphanumeric not required. Lockout 5 failed attempts → 5 min. Unique email.

### 4.2 Policies
| Policy | Definition |
|---|---|
| `AdminOnly` | role = Admin |
| `Operator` | role = Admin, OR (role = User AND no `account_id` claim) |
| `Portal` | role = User AND has `account_id` claim |
| `Authenticated` | any valid token |

Interpretation of the assignment: "a Company can be a user" → users belong to a company and act on its behalf (Operator). "A Current Account can be a user" → a User linked to a CurrentAccount is a **portal user** who only sees his own account statement and invoices.

### 4.3 `ICurrentUser` (Application/Common/Abstractions; implemented in Api from HttpContext)
`int? UserId; string? Email; bool IsAuthenticated; bool IsAdmin; int? CompanyId /* claim, or X-Company-Id for admin */; int? CurrentAccountId; string? IpAddress`.

---

## 5. REST API

Base URL: `http://localhost:5080/api` (local), all JSON camelCase, enums as strings, `DateOnly` as `"yyyy-MM-dd"`, `DateTime` as ISO-8601 UTC with `Z`, `rowVersion` as Base64 string. Controllers (`[ApiController]`), route prefix `api/`. Swagger UI at `/swagger` with JWT "Authorize" button and XML comments.

### 5.1 Common: paging, sorting, filtering
Query params on every list endpoint:
| Param | Default | Rules |
|---|---|---|
| `page` | 1 | ≥ 1 |
| `pageSize` | 20 | 1..100 (larger → 400) |
| `sort` | per entity | comma-separated fields, `-` prefix = desc, e.g. `sort=-invoiceDate,documentNo`. Unknown field → 400 |
| `q` | – | free-text "contains" search (case-insensitive) over listed fields |

Response envelope `PagedResult<T>`:
```json
{ "items": [ ... ], "page": 1, "pageSize": 20, "totalCount": 57, "totalPages": 3 }
```
Ties always broken by `id` asc for stable paging.

### 5.2 Error format (RFC 7807 ProblemDetails, `application/problem+json`)
```json
{
  "type": "https://httpstatuses.io/422",
  "title": "Business rule violation",
  "status": 422,
  "detail": "Yetersiz stok: ELMA",
  "instance": "/api/invoices/12/approve",
  "code": "INSUFFICIENT_STOCK",
  "traceId": "00-...",
  "correlationId": "c7a1...",
  "errors": { "lines[0].quantity": ["Miktar 0'dan büyük olmalıdır."] }
}
```
`errors` only for 400 validation (keys camelCase property paths). `detail` messages are Turkish (shown in UI). Unhandled exceptions → 500, `code: "INTERNAL_ERROR"`, no stack trace outside Development.

Status / code mapping:
| Status | code(s) |
|---|---|
| 400 | `VALIDATION_FAILED`, `COMPANY_REQUIRED` |
| 401 | `UNAUTHORIZED`, `INVALID_CREDENTIALS`, `ACCOUNT_LOCKED`, `USER_INACTIVE`, `INVALID_REFRESH_TOKEN` |
| 403 | `FORBIDDEN` |
| 404 | `NOT_FOUND` |
| 409 | `CONCURRENCY_CONFLICT`, `DUPLICATE_CODE`, `DUPLICATE_EMAIL`, `DUPLICATE_TAX_NUMBER`, `INVOICE_NOT_DRAFT`, `INVOICE_NOT_APPROVED`, `STOCK_HAS_BALANCE`, `STOCK_IN_USE`, `ACCOUNT_HAS_BALANCE`, `ACCOUNT_IN_USE`, `COMPANY_NOT_EMPTY`, `CANNOT_DELETE_SELF` |
| 422 | `INSUFFICIENT_STOCK`, `INVOICE_HAS_NO_LINES`, `INACTIVE_STOCK`, `INACTIVE_ACCOUNT`, `ACCOUNT_TYPE_MISMATCH`, `INVALID_REFERENCE` |
| 500 | `INTERNAL_ERROR` |
Application exceptions: `NotFoundException`, `ConflictException(code,msg)`, `BusinessRuleException(code,msg)`, `ForbiddenException`, FluentValidation `ValidationException`; `DomainException` maps by code per table above. Global handler = `IExceptionHandler` + `AddProblemDetails()`. 401/403 from the auth middleware also return ProblemDetails.

### 5.3 Common headers
- Request: `Authorization: Bearer <access>`, optional `X-Correlation-Id` (echoed back; generated if absent), `X-Company-Id` (admin only), `If-None-Match`.
- Response: `X-Correlation-Id`; on stock/current-account GET endpoints `X-Cache: HIT|MISS`.
- **ETag**: every `GET` returning 200 JSON on `/api/**` (except `/api/auth/**`) gets a weak ETag `W/"<first 16 hex of SHA-256 of body>"` and `Cache-Control: private, no-cache`. If request `If-None-Match` matches → `304 Not Modified` with empty body. Implemented as a global result/endpoint filter.
- Optimistic concurrency on writes uses `rowVersion` in the body (not If-Match).

### 5.4 Auth — `/api/auth`
| Method | Route | Policy | Body | Response |
|---|---|---|---|---|
| POST | `/auth/login` | anonymous | `{ email, password }` | 200 `AuthResponse`; 401 `INVALID_CREDENTIALS`/`ACCOUNT_LOCKED`/`USER_INACTIVE`; 400 |
| POST | `/auth/refresh` | anonymous | `{ refreshToken }` | 200 `AuthResponse`; 401 `INVALID_REFRESH_TOKEN` |
| POST | `/auth/logout` | Authenticated | `{ refreshToken }` | 204 (revokes it; idempotent) |
| GET | `/auth/me` | Authenticated | – | 200 `UserInfo` |
| POST | `/auth/change-password` | Authenticated | `{ currentPassword, newPassword }` | 204; 400 |

```ts
AuthResponse { accessToken: string; accessTokenExpiresAt: string; refreshToken: string; refreshTokenExpiresAt: string; user: UserInfo }
UserInfo { id: number; email: string; fullName: string; role: "Admin"|"User"; companyId: number|null; companyName: string|null; currentAccountId: number|null; currentAccountTitle: string|null; isPortalUser: boolean }
```

### 5.5 Companies — `/api/companies`
| Method | Route | Policy | Request | Response |
|---|---|---|---|---|
| GET | `/companies` | AdminOnly | list params; filters `q` (name, taxNumber, city), `isActive`; sort fields `name,taxNumber,city,createdAt` (default `name`) | 200 `PagedResult<CompanyDto>` |
| GET | `/companies/{id}` | AdminOnly | – | 200 `CompanyDto` / 404 |
| GET | `/companies/current` | Operator | – (user's company; admin: X-Company-Id company, else 404) | 200 `CompanyDto` |
| POST | `/companies` | AdminOnly | `CompanySaveRequest` | 201 `CompanyDto` + Location; 400; 409 `DUPLICATE_TAX_NUMBER` |
| PUT | `/companies/{id}` | AdminOnly | `CompanySaveRequest` + `rowVersion` (required) | 200 `CompanyDto`; 404; 409 |
| DELETE | `/companies/{id}` | AdminOnly | – | 204; 404; 409 `COMPANY_NOT_EMPTY` |

```ts
CompanySaveRequest { name; taxNumber; taxOffice?; phone?; email?; address?; city?; isActive: boolean; rowVersion?: string /* required on PUT */ }
CompanyDto { id; name; taxNumber; taxOffice; phone; email; address; city; isActive; createdAt; updatedAt; rowVersion }
```

### 5.6 Users — `/api/users` (AdminOnly for all)
| Method | Route | Request | Response |
|---|---|---|---|
| GET | `/users` | filters `q` (email, fullName), `role`, `companyId`, `isActive`; sort `email,fullName,createdAt,lastLoginAt` (default `email`) | `PagedResult<UserDto>` |
| GET | `/users/{id}` | – | `UserDto` |
| POST | `/users` | `UserCreateRequest` | 201 `UserDto`; 409 `DUPLICATE_EMAIL`; 400 |
| PUT | `/users/{id}` | `UserUpdateRequest` | 200 `UserDto` |
| DELETE | `/users/{id}` | – | 204; 409 `CANNOT_DELETE_SELF` |

```ts
UserCreateRequest { email; fullName; password; role: "Admin"|"User"; companyId?: number; currentAccountId?: number; isActive: boolean }
UserUpdateRequest { fullName; role; companyId?; currentAccountId?; isActive; newPassword?: string /* if set, resets password */ }
UserDto { id; email; fullName; role; companyId; companyName; currentAccountId; currentAccountTitle; isActive; createdAt; lastLoginAt }
```
Validation: role User → companyId required; role Admin → companyId and currentAccountId must be null; currentAccountId must belong to companyId and not be linked to another user (409 `ACCOUNT_IN_USE`). Deactivating a user revokes his refresh tokens.

### 5.7 Stocks — `/api/stocks`
| Method | Route | Policy | Request | Response |
|---|---|---|---|---|
| GET | `/stocks` | Operator | filters `q` (code, name, barcode), `unit`, `isActive`; sort `code,name,unit,unitPrice,balance,createdAt` (default `code`) | `PagedResult<StockDto>` (cached) |
| GET | `/stocks/{id}` | Operator | – | `StockDto` (cached) / 404 |
| POST | `/stocks` | Operator | `StockSaveRequest` | 201 `StockDto`; 409 `DUPLICATE_CODE` |
| PUT | `/stocks/{id}` | Operator | `StockSaveRequest` (rowVersion required) | 200 `StockDto`; 409 |
| DELETE | `/stocks/{id}` | **AdminOnly** | – | 204; 409 `STOCK_HAS_BALANCE`/`STOCK_IN_USE` |
| GET | `/stocks/{id}/transactions` | Operator | list params + `direction`, `dateFrom`, `dateTo`; default sort `-transDate,-id` | `PagedResult<StockTransDto>` |

```ts
StockSaveRequest { code; name; unit: StockUnit; barcode?; vatRate: number; unitPrice: number; description?; isActive: boolean; rowVersion?: string }
StockDto { id; companyId; companyName; code; name; unit; barcode; vatRate; unitPrice; description; isActive; balance; createdAt; updatedAt; rowVersion }
```

### 5.8 Current accounts — `/api/current-accounts`
| Method | Route | Policy | Request | Response |
|---|---|---|---|---|
| GET | `/current-accounts` | Operator | filters `q` (code, title, taxNumber), `type`, `city`, `isActive`; sort `code,title,type,city,balance,createdAt` (default `code`) | `PagedResult<CurrentAccountDto>` (cached) |
| GET | `/current-accounts/{id}` | Operator | – | `CurrentAccountDto` (cached) |
| POST | `/current-accounts` | Operator | `CurrentAccountSaveRequest` | 201; 409 `DUPLICATE_CODE` |
| PUT | `/current-accounts/{id}` | Operator | `CurrentAccountSaveRequest` (rowVersion required) | 200 |
| DELETE | `/current-accounts/{id}` | **AdminOnly** | – | 204; 409 `ACCOUNT_HAS_BALANCE`/`ACCOUNT_IN_USE` |
| GET | `/current-accounts/{id}/transactions` | Operator | list params + `direction`, `dateFrom`, `dateTo`; default sort `-transDate,-id` | `PagedResult<ActTransDto>` |

```ts
CurrentAccountSaveRequest { code; title; type: CurrentAccountType; taxNumber?; taxOffice?; phone?; email?; address?; city?; isActive: boolean; rowVersion?: string }
CurrentAccountDto { id; companyId; companyName; code; title; type; taxNumber; taxOffice; phone; email; address; city; isActive; balance; createdAt; updatedAt; rowVersion }
```

### 5.9 Invoices — `/api/invoices`
| Method | Route | Policy | Request | Response |
|---|---|---|---|---|
| GET | `/invoices` | Operator | filters `q` (documentNo, account title/code, description), `type`, `status`, `currentAccountId`, `series`, `dateFrom`, `dateTo`; sort `invoiceDate,documentNo,grandTotal,status,createdAt` (default `-invoiceDate,-id`) | `PagedResult<InvoiceListItemDto>` |
| GET | `/invoices/{id}` | Operator | – | `InvoiceDto` (with lines) |
| POST | `/invoices` | Operator | `InvoiceSaveRequest` | 201 `InvoiceDto` (status Draft) |
| PUT | `/invoices/{id}` | Operator | `InvoiceSaveRequest` (rowVersion required; replaces header and **all** lines) | 200 `InvoiceDto`; 409 `INVOICE_NOT_DRAFT` |
| DELETE | `/invoices/{id}` | Operator | – | 204; 409 `INVOICE_NOT_DRAFT` |
| POST | `/invoices/{id}/approve` | Operator | `{ rowVersion }` | 200 `InvoiceDto`; 409; 422 |
| POST | `/invoices/{id}/cancel` | **AdminOnly** | `{ rowVersion, reason }` | 200 `InvoiceDto`; 409 `INVOICE_NOT_APPROVED`; 422 |

```ts
InvoiceSaveRequest { type: InvoiceType /* ignored on PUT */; series?: string; invoiceDate: string; dueDate?: string; currentAccountId: number; description?: string; lines: InvoiceLineRequest[]; rowVersion?: string }
InvoiceLineRequest { stockId: number; description?: string; quantity: number; unitPrice: number; vatRate: number }
InvoiceDto { id; companyId; companyName; type; status; series; number: number|null; documentNo: string|null; invoiceDate; dueDate; currentAccountId; currentAccountCode; currentAccountTitle; description; subTotal; vatTotal; grandTotal; approvedAt; cancelledAt; cancelReason; createdAt; updatedAt; rowVersion; lines: InvoiceLineDto[] }
InvoiceLineDto { id; lineNo; stockId; stockCode; stockName; unit; description; quantity; unitPrice; vatRate; lineNet; vatAmount; lineTotal }
InvoiceListItemDto { id; companyId; companyName; type; status; series; documentNo; invoiceDate; currentAccountId; currentAccountCode; currentAccountTitle; subTotal; vatTotal; grandTotal; lineCount; createdAt }
```
Validation: 1..200 lines required on every save (0 lines → 400), quantity > 0 (max 3 decimals), unitPrice ≥ 0, vatRate ∈ {0,1,10,20}, invoiceDate required, dueDate ≥ invoiceDate, description ≤ 500.

### 5.10 Transactions, dashboard, portal, admin
| Method | Route | Policy | Request | Response |
|---|---|---|---|---|
| GET | `/stock-trans` | Operator | filters `stockId`, `invoiceId`, `direction`, `dateFrom`, `dateTo`, `q` (stock code/name, description); sort `transDate,quantity,amount,createdAt` (default `-transDate,-id`) | `PagedResult<StockTransDto>` |
| GET | `/act-trans` | Operator | filters `currentAccountId`, `invoiceId`, `direction`, `dateFrom`, `dateTo`, `q` (account code/title, description); same sorts | `PagedResult<ActTransDto>` |
| GET | `/dashboard/summary` | Operator | – | `DashboardSummaryDto` |
| GET | `/me/account` | Portal | – | `CurrentAccountDto` (own account) |
| GET | `/me/transactions` | Portal | list params + `dateFrom`, `dateTo` | `PagedResult<ActTransDto>` |
| GET | `/me/invoices` | Portal | list params + `status` (only Approved/Cancelled are ever returned) | `PagedResult<InvoiceListItemDto>` |
| GET | `/me/invoices/{id}` | Portal | – | `InvoiceDto` (404 if not own or Draft) |
| POST | `/admin/reconciliation/run?fix=false` | AdminOnly | – | `ReconciliationResultDto` |
| GET | `/health` (no `/api` prefix) | anonymous | – | 200 Healthy / 503 (SQL + Redis checks; Redis "Degraded" if fallback) |

```ts
StockTransDto { id; companyId; stockId; stockCode; stockName; unit; invoiceId; documentNo; transDate; direction: "In"|"Out"; quantity; unitPrice; amount; isReversal; description; createdAt }
ActTransDto { id; companyId; currentAccountId; currentAccountCode; currentAccountTitle; invoiceId; documentNo; transDate; direction: "Debit"|"Credit"; amount; isReversal; description; createdAt }
DashboardSummaryDto { stockCount; currentAccountCount; draftInvoiceCount; approvedInvoiceCountThisMonth; salesTotalThisMonth; purchaseTotalThisMonth; totalReceivable /* Σ positive account balances */; totalPayable /* Σ |negative balances| */; lowestStocks: { id; code; name; unit; balance }[] /* 5 lowest balances among active stocks */; recentInvoices: InvoiceListItemDto[] /* last 5 by createdAt */ }
ReconciliationResultDto { checkedAt; stocksChecked; accountsChecked; fixed: boolean; discrepancies: { entityType: "Stock"|"CurrentAccount"; entityId; companyId; code; storedBalance; ledgerBalance; difference }[] }
```
"This month" = current UTC calendar month by InvoiceDate, Approved invoices only.

---

## 6. Caching (server)

Abstraction `ICacheService` (Application/Common/Caching), impl in Infrastructure over `IDistributedCache` with System.Text.Json:
`GetAsync<T>`, `SetAsync<T>(key, value, ttl)`, `RemoveAsync(key)`, `GetVersionAsync(entity, scope)`, `BumpVersionAsync(entity, scope)`. **All cache errors are caught and logged (Warning); a cache failure never fails a request.**

Provider: if `Cache:UseRedis` = true and Redis reachable at startup (connect timeout 2 s) → `AddStackExchangeRedisCache` with `InstanceName = "sa:"`; otherwise log Warning and use `AddDistributedMemoryCache`. `Cache:Enabled=false` → no-op cache (always MISS).

Keys (after the `sa:` instance prefix). `{entity}` ∈ `stock`, `account`. `{scope}` = company id, or `all` for admin without X-Company-Id.
| Key | Value | TTL |
|---|---|---|
| `{entity}:{id}` | StockDto / CurrentAccountDto | 10 min absolute (`Cache:EntityTtlMinutes`) |
| `{entity}:ver:{scope}` | random token (Guid "N") | 24 h sliding (recreated if missing) |
| `{entity}:list:{scope}:{verToken}:{queryHash}` | PagedResult DTO | 5 min absolute (`Cache:ListTtlMinutes`) |
`queryHash` = first 16 hex chars of SHA-256 of the normalized query (lower-cased param names, sorted, defaults filled in: page, pageSize, sort, q, filters).
Tenant check on cached by-id entries is done *after* reading (compare `companyId`), so a by-id key is shared safely.

Invalidation (after successful commit):
| Trigger | Action |
|---|---|
| Stock create/update/delete | remove `stock:{id}`; bump `stock:ver:{companyId}` and `stock:ver:all` |
| CurrentAccount create/update/delete | same with `account` |
| Invoice approve/cancel | for each affected stock: remove `stock:{id}`; bump stock versions; remove `account:{accountId}`; bump account versions |
| Company delete/update | bump `stock:ver:*` and `account:ver:*` for that company and `all` (companyName in DTOs) |
Invoice draft CRUD does not touch balances → no stock/account invalidation. Invoices, transactions and dashboard are **not** server-cached (always fresh).

---

## 7. Logging & observability
- Serilog configured from `appsettings.json` `Serilog` section via `builder.Host.UseSerilog((ctx, sp, cfg) => cfg.ReadFrom.Configuration(...))` with bootstrap logger.
- Sinks: Console (`[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}`), File `logs/api-.log` rolling **daily**, `retainedFileCountLimit: 14`, compact JSON or text template incl. CorrelationId.
- Enrichers: FromLogContext, MachineName, ThreadId, plus `CorrelationId`, `UserId`, `CompanyId` pushed by `CorrelationIdMiddleware`/request logging.
- `app.UseSerilogRequestLogging()` with enriched diagnostic context (UserId, ClientIp).
- Minimum levels: Default Information; Microsoft.AspNetCore Warning; Microsoft.EntityFrameworkCore.Database.Command Warning.
- Business events logged at Information: login success/failure, invoice approved/cancelled (DocumentNo, totals), cache fallback, reconciliation summary.

---

## 8. Configuration

### 8.1 `src/StockAccount.Api/appsettings.json` (local defaults)
```json
{
  "ConnectionStrings": {
    "Default": "Server=.\\SQLEXPRESS;Database=StockAccountDb;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False",
    "Redis": "localhost:6379,abortConnect=false,connectTimeout=2000"
  },
  "Jwt": { "Issuer": "StockAccount", "Audience": "StockAccount.Clients", "Key": "DEV-ONLY-change-me-0123456789-abcdefghijklmnop", "AccessTokenMinutes": 15, "RefreshTokenDays": 7 },
  "Cache": { "Enabled": true, "UseRedis": true, "EntityTtlMinutes": 10, "ListTtlMinutes": 5 },
  "Inventory": { "AllowNegativeStock": false },
  "Reconciliation": { "Enabled": true, "IntervalMinutes": 5, "AutoFix": false },
  "Database": { "MigrateOnStartup": true, "Seed": true },
  "Cors": { "AllowedOrigins": [ "http://localhost:5173", "http://localhost:3000" ] },
  "Serilog": { "...": "see section 7" }
}
```
CORS: allowed origins from config, any header/method, expose headers `X-Correlation-Id, X-Cache, ETag, Location`.

### 8.2 Ports
| Component | Local dev | Docker (host:container) |
|---|---|---|
| API | http://localhost:5080 (launchSettings, http only) | 5080:8080 |
| ui-vue | http://localhost:5173 (vite) | 5173:80 (nginx static) |
| ui-nuxt | http://localhost:3000 (nuxt dev) | 3000:3000 (node `.output/server/index.mjs`) |
| SQL Server | `.\SQLEXPRESS` | 14333:1433 (avoid clash with local SQL) |
| Redis | localhost:6379 (run `docker run -d --name sa-redis -p 6379:6379 redis:7-alpine`, optional) | 6379:6379 |

### 8.3 Docker compose (`docker-compose.yml` at root; `.env` from `.env.example`)
Services: `mssql` (`mcr.microsoft.com/mssql/server:2022-latest`, `ACCEPT_EULA=Y`, `MSSQL_SA_PASSWORD=${MSSQL_SA_PASSWORD}`, healthcheck via `/opt/mssql-tools18/bin/sqlcmd -C ... -Q "SELECT 1"`, volume `mssql-data`), `redis` (`redis:7-alpine`, healthcheck `redis-cli ping`), `api` (build `src/StockAccount.Api/Dockerfile` with context root; depends_on healthy mssql+redis), `ui-vue` (build `ui-vue/`, build arg `VITE_API_BASE_URL=http://localhost:5080/api`), `ui-nuxt` (build `ui-nuxt/`, env `NUXT_PUBLIC_API_BASE=http://localhost:5080/api`).
API env in compose:
```
ASPNETCORE_ENVIRONMENT=Docker
ASPNETCORE_URLS=http://+:8080
ConnectionStrings__Default=Server=mssql,1433;Database=StockAccountDb;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True
ConnectionStrings__Redis=redis:6379,abortConnect=false,connectTimeout=2000
Jwt__Key=${JWT_KEY}
Database__MigrateOnStartup=true
Database__Seed=true
```
`.env.example`: `MSSQL_SA_PASSWORD=StockAccount!2026`, `JWT_KEY=docker-change-me-0123456789-abcdefghijklmnopqrst`.
API startup with MigrateOnStartup must retry DB connection (EnableRetryOnFailure + up to 10 attempts × 5 s for the initial migrate).

### 8.4 Frontend env
- ui-vue: `VITE_API_BASE_URL` (default `http://localhost:5080/api`) in `.env.development`.
- ui-nuxt: `runtimeConfig.public.apiBase` (env `NUXT_PUBLIC_API_BASE`, default `http://localhost:5080/api`).

---

## 9. Seed data (idempotent; runs when `Database:Seed` = true)
| Item | Value |
|---|---|
| Roles | `Admin`, `User` |
| Admin | `admin@stockaccount.local` / `Admin123!`, FullName "Sistem Yöneticisi", role Admin, no company |
| Company | "Demo Gıda A.Ş.", TaxNumber `1234567890`, TaxOffice "Kadıköy", City "İstanbul" |
| Operator user | `demo@stockaccount.local` / `Demo123!`, "Demo Kullanıcı", role User, company Demo |
| Stocks (Demo) | `ELMA` Elma Kg VAT 1 price 50.00; `ARMUT` Armut Kg VAT 1 price 60.00; `SUT` Süt 1L Liter VAT 1 price 35.00; `EKMEK` Ekmek Piece VAT 1 price 15.00; `DETERJAN` Deterjan 3kg Piece VAT 20 price 250.00 |
| Accounts (Demo) | `C001` Ahmet Yılmaz Customer City İstanbul; `C002` Ayşe Demir Customer City Ankara; `S001` Bereket Tarım Ltd. Şti. Supplier TaxNumber `9876543210`; `B001` Karma Ticaret A.Ş. Both |
| Portal user | `ahmet@stockaccount.local` / `Ahmet123!`, "Ahmet Yılmaz", role User, company Demo, CurrentAccount C001 |
| Opening purchase | Purchase invoice, series `ALS`, account S001, date = seed day, description "Açılış alımı (seed)", lines: ELMA 100 × 30, ARMUT 80 × 40, SUT 200 × 20, EKMEK 150 × 8, DETERJAN 40 × 150, VAT = stock VatRate. **Approved** through `IInvoiceService.ApproveAsync` (Backend startup) so ledgers/balances are consistent → ELMA balance 100, S001 balance negative. |
DB agent's `DataSeeder` creates everything above with the purchase invoice as **Draft**; Backend's startup step approves any Draft invoice whose Description is exactly "Açılış alımı (seed)" after seeding.
Seed is idempotent: check by unique keys (email, tax number, code) before inserting; opening invoice only created if the company has no invoices.

---

## 10. Frontend (ui-vue and ui-nuxt — identical behavior, texts, routes and test ids)

### 10.1 Stack & structure
Vue 3 `<script setup lang="ts">`, PrimeVue 4 with **Aura** preset (`@primeuix/themes/aura`), PrimeFlex 4 utilities for layout, primeicons, Pinia, vue-router 4 (Nuxt: file-based pages with same paths). PrimeVue Turkish locale (day/month names, `firstDayOfWeek: 1`, `dateFormat: 'dd.mm.yy'`). Services: ToastService, ConfirmationService. Responsive: sidebar menu collapses into a top-bar toggle below 992px.
ui-vue folders: `src/api/` (http client, cache, per-resource modules), `src/stores/` (auth, ui), `src/views/`, `src/components/`, `src/router/`, `src/types/` (TS mirrors of DTOs in section 5), `src/utils/format.ts`, `src/i18n/labels.ts`.
Nuxt: `ssr: false` (SPA mode; tokens live in the browser), `@primevue/nuxt-module`, `@pinia/nuxt`, `middleware/auth.global.ts`, `plugins/api.ts`; same component code ported.

### 10.2 Auth flow
- Login → store `accessToken`, `refreshToken`, `accessTokenExpiresAt`, `user` in Pinia, persisted to `localStorage` keys `sa.accessToken`, `sa.refreshToken`, `sa.accessTokenExpiresAt`, `sa.user`. Admin's selected company: `sa.companyId`.
- Axios request interceptor adds `Authorization`, and `X-Company-Id` (admin, if selected). If the access token expires in < 60 s, refresh proactively first.
- Response interceptor: on 401 (except `/auth/*`), perform **single-flight** refresh (concurrent 401s wait for the same promise), retry original once; refresh failure → clear storage + HTTP cache, redirect `/login?redirect=<path>`.
- Logout: POST `/auth/logout`, clear storage and HTTP cache, go `/login`.
- Route guard: meta `{ public?: true, policy?: 'operator'|'admin'|'portal' }`. Unauthenticated → `/login`. Policy mismatch → `/403`. After login: portal user → `/my-account`, others → `/` (or `redirect` query).

### 10.3 HTTP cache (client) — custom layer in `src/api/http.ts` (Nuxt: `utils/http.ts`)
- Applies to `GET` only. Key = method + full URL with sorted query string + `X-Company-Id`.
- Entry `{ data, etag, expiresAt }` in an in-memory `Map` (max 200 entries, LRU eviction).
- Fresh (now < expiresAt) → return cached data without network. Stale with etag → send `If-None-Match`; on **304** reuse data and renew expiry (axios `validateStatus: s => (s >= 200 && s < 300) || s === 304`).
- Identical in-flight GETs are de-duplicated (share one promise).
- TTLs by URL prefix: `/stocks`, `/current-accounts` lists & details 60 s; lookups for autocompletes (any request with `pageSize=10` from AutoComplete) 300 s; `/companies` 300 s; `/invoices` 30 s; `/stock-trans`, `/act-trans`, `/*/transactions` 30 s; `/dashboard` 30 s; `/me/*` 30 s; `/auth/*` never cached.
- Invalidation `invalidate(prefixes: string[])` after successful mutations:
  | Mutation | Invalidated prefixes |
  |---|---|
  | stock C/U/D | `/stocks`, `/dashboard` |
  | current account C/U/D | `/current-accounts`, `/dashboard` |
  | invoice create/update/delete | `/invoices`, `/dashboard` |
  | invoice approve/cancel | `/invoices`, `/stocks`, `/current-accounts`, `/stock-trans`, `/act-trans`, `/dashboard`, `/me` |
  | company C/U/D | `/companies`, `/stocks`, `/current-accounts`, `/invoices` |
  | user C/U/D | `/users` |
  | logout, login, company switch | clear all |
- Every list page has a "Yenile" (refresh) button that bypasses cache (`{ force: true }`).

### 10.4 Routes / pages
| Path | Policy | Page (Turkish title) | Endpoints |
|---|---|---|---|
| `/login` | public | Giriş | POST /auth/login |
| `/` | operator | Gösterge Paneli | GET /dashboard/summary |
| `/stocks` | operator | Stoklar | GET /stocks, DELETE /stocks/{id} (admin only button) |
| `/stocks/new`, `/stocks/:id` | operator | Stok Kartı (form + "Hareketler" tab on edit) | GET/POST/PUT /stocks, GET /stocks/{id}/transactions |
| `/current-accounts` | operator | Cari Hesaplar | GET /current-accounts, DELETE |
| `/current-accounts/new`, `/current-accounts/:id` | operator | Cari Kart (form + "Hareketler" tab) | GET/POST/PUT, GET /current-accounts/{id}/transactions |
| `/invoices` | operator | Faturalar | GET /invoices, DELETE |
| `/invoices/new`, `/invoices/:id` | operator | Fatura (editable if Draft, read-only otherwise; Onayla / İptal Et buttons) | GET/POST/PUT /invoices, POST approve/cancel, GET /stocks & /current-accounts (AutoComplete) |
| `/stock-transactions` | operator | Stok Hareketleri | GET /stock-trans |
| `/account-transactions` | operator | Cari Hareketleri | GET /act-trans |
| `/companies` (+ dialog form) | admin | Firmalar | /companies CRUD |
| `/users` (+ dialog form) | admin | Kullanıcılar | /users CRUD, GET /companies, GET /current-accounts (with X-Company-Id of chosen company) |
| `/reconciliation` | admin | Bakiye Mutabakatı | POST /admin/reconciliation/run |
| `/my-account` | portal | Hesabım (balance card, transactions table, invoices table with detail dialog) | /me/account, /me/transactions, /me/invoices, /me/invoices/{id} |
| `/profile` | any auth | Profilim (change password) | GET /auth/me, POST /auth/change-password |
| `/403`, `/:pathMatch(.*)*` | public | Yetkisiz Erişim / Sayfa Bulunamadı | – |

Admin top bar has a company switcher (Select, "Tüm Firmalar" + companies) setting `sa.companyId`. Creating a stock/account/invoice as admin with "Tüm Firmalar" selected shows warning "Lütfen önce firma seçiniz." and disables the "Yeni" buttons.

List pages: PrimeVue DataTable `lazy` mode, server paging (rows options 10/20/50), server sort, filter bar above table (search InputText debounced 400 ms + per-entity Selects/DatePickers), paginator, loading skeleton, empty message "Kayıt bulunamadı.". Delete uses ConfirmDialog (header "Silme Onayı", accept "Evet", reject "Hayır"). Success → Toast severity success, summary "Başarılı"; API error → Toast severity error, summary "Hata", detail = ProblemDetails.detail (or first validation error). 400 validation errors are shown under the corresponding form fields.

Invoice form: header (Tür, Seri, Tarih, Vade, Cari [AutoComplete "code - title", filtered by type compatibility], Açıklama), lines grid (Stok AutoComplete "code - name" which pre-fills unitPrice & vatRate from the stock, Miktar, Birim Fiyat, KDV %, computed Tutar/KDV/Toplam), live totals using the same formulas/rounding as server, "Satır Ekle" button. Approve asks confirmation ("Fatura onaylanacak, stok ve cari hareketleri oluşacak. Emin misiniz?"). Cancel opens a dialog with required reason.

### 10.5 Labels & formatting
Enum labels: Sales "Satış", Purchase "Alış"; Draft "Taslak", Approved "Onaylandı", Cancelled "İptal Edildi"; Piece "Adet", Kg "Kg", Gram "Gram", Liter "Litre", Meter "Metre", Box "Koli"; Customer "Müşteri", Supplier "Tedarikçi", Both "Müşteri/Tedarikçi"; In "Giriş", Out "Çıkış", Debit "Borç", Credit "Alacak". Status tag severities: Draft warn, Approved success, Cancelled danger.
Money: `Intl.NumberFormat('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })` + " ₺". Quantity: `tr-TR`, 0–3 fraction digits, + unit label. Dates: `dd.MM.yyyy`. Account balance shows "(B)" if > 0 and "(A)" if < 0.

### 10.6 Test ids (binding for E2E; identical in both UIs)
Rule: `data-testid` goes on the native `<input>/<button>` where PrimeVue allows a direct attribute (InputText, Button, Textarea, Checkbox via `inputId` wrapper not needed); for **composite components** (Select, AutoComplete, InputNumber, DatePicker, ToggleSwitch) put the test id on a wrapping `<div>` that contains only that component. E2E types with `getByTestId(id).locator('input').first()` and opens Selects by clicking the wrapper then `getByRole('option', { name })`. Elements that display numbers additionally carry `data-value` = raw invariant number string (`String(n)`, e.g. `"101"`, `"98"`, `"-3900"`).

| Area | Test ids |
|---|---|
| Login | `login-email`, `login-password`, `login-submit`, `login-error` |
| Layout | `nav-dashboard`, `nav-stocks`, `nav-current-accounts`, `nav-invoices`, `nav-stock-transactions`, `nav-account-transactions`, `nav-companies`, `nav-users`, `nav-reconciliation`, `nav-my-account`, `user-menu`, `logout-button`, `company-switcher`, `page-title` |
| Lists (prefix P ∈ `stock`, `account`, `invoice`, `company`, `user`, `stock-trans`, `act-trans`) | `{P}-table`, `{P}-search`, `{P}-new`, `{P}-refresh`; per row buttons `row-edit`, `row-delete`, `row-view` (located within the row) |
| Extra list filters | `stock-filter-unit`, `stock-filter-active`, `account-filter-type`, `invoice-filter-type`, `invoice-filter-status`, `invoice-filter-date-from`, `invoice-filter-date-to` |
| Stock form | `stock-code`, `stock-name`, `stock-unit`, `stock-barcode`, `stock-vat-rate`, `stock-unit-price`, `stock-description`, `stock-is-active`, `stock-balance`, `form-save`, `form-back`, `tab-transactions` |
| Account form | `account-code`, `account-title`, `account-type`, `account-tax-number`, `account-tax-office`, `account-phone`, `account-email`, `account-city`, `account-address`, `account-is-active`, `account-balance`, `form-save`, `form-back`, `tab-transactions` |
| Invoice form | `invoice-type`, `invoice-series`, `invoice-date`, `invoice-due-date`, `invoice-account`, `invoice-description`, `invoice-add-line`, `line-{i}-stock`, `line-{i}-quantity`, `line-{i}-unit-price`, `line-{i}-vat-rate`, `line-{i}-total`, `line-{i}-remove` (i is 0-based), `invoice-subtotal`, `invoice-vat-total`, `invoice-grand-total`, `invoice-status`, `invoice-document-no`, `invoice-save`, `invoice-approve`, `invoice-cancel`, `invoice-delete`, `cancel-reason`, `cancel-confirm` |
| Company dialog | `company-name`, `company-tax-number`, `company-tax-office`, `company-city`, `company-is-active`, `form-save` |
| User dialog | `user-email`, `user-full-name`, `user-password`, `user-role`, `user-company`, `user-current-account`, `user-is-active`, `form-save` |
| Dashboard | `dash-stock-count`, `dash-account-count`, `dash-draft-count`, `dash-sales-total`, `dash-receivable` |
| Portal | `my-balance`, `my-transactions-table`, `my-invoices-table` |
| Reconciliation | `recon-run`, `recon-fix`, `recon-result`, `recon-discrepancy-count` |
| Confirm dialog | use role button names "Evet" / "Hayır" |
| Toasts | rely on text "Başarılı" / "Hata" |

---

## 11. Test scope

### 11.1 Unit tests (`tests/StockAccount.UnitTests`)
- Line/invoice calculation & rounding (incl. 2 × 50 @1% = 101.00; 3 × 33.3333 @20%; midpoint rounding).
- Status rules (`EnsureDraft`), validators (stock, account, invoice, user role/company rules, paging params).
- InvoiceService approve/cancel with EF InMemory or SQLite: ledger rows, balances, numbering sequence, INSUFFICIENT_STOCK, ACCOUNT_TYPE_MISMATCH, cancel reversal.
- Cache key/queryHash normalization (same params different order → same hash); cache service swallows exceptions.
- Sort parser rejects unknown fields.

### 11.2 Integration tests (`tests/StockAccount.IntegrationTests`)
`WebApplicationFactory<Program>` (`public partial class Program {}` required in Api). DB: SQL Server from env `TEST_SQL_CONNECTION`, default `Server=.\SQLEXPRESS;Database=StockAccountTests_{guid};Trusted_Connection=True;TrustServerCertificate=True`; database migrated + seeded per test run (collection fixture) and dropped at end. Cache: `Cache:UseRedis=false` (in-memory distributed cache). Reconciliation background service disabled (`Reconciliation:Enabled=false`).
Scenarios:
1. Auth: login ok/invalid (401 INVALID_CREDENTIALS), /auth/me, refresh rotation (old token rejected afterwards), reuse detection revokes new token too, logout, change password.
2. Authorization matrix: anonymous 401; operator → /companies 403, DELETE /stocks 403; portal (ahmet) → /stocks 403, /me/account 200; admin all.
3. Tenant isolation: admin creates Company B + operator B; operator B gets 404 for Demo's stock id and sees only own lists; admin with X-Company-Id sees filtered lists; admin create without company → 400 COMPANY_REQUIRED.
4. CRUD + validation for companies, users, stocks, current accounts, invoices (400 with `errors`, 409 DUPLICATE_CODE, 404).
5. Paging/sort/filter: totalCount/totalPages, `sort=-code`, `q`, pageSize 101 → 400, unknown sort → 400.
6. Concurrency: PUT with stale rowVersion → 409 CONCURRENCY_CONFLICT.
7. Cache: GET /stocks/{id} → X-Cache MISS then HIT; after PUT → MISS with new values; after invoice approve → stock balance fresh.
8. ETag: second GET with If-None-Match → 304.
9. **Ahmet/Apple**: read ELMA & C001 balances; create Sales invoice (C001, ELMA 2 × 50 @1) → 201 Draft, totals 100/1/101, number null; approve → 200 Approved, DocumentNo `SAT` + 6 digits; StockTrans has one Out 2.000 row for ELMA with invoiceId; ActTrans has one Debit 101.00 row for C001; ELMA balance −2, C001 balance +101.
10. Rules: PUT/DELETE approved → 409 INVOICE_NOT_DRAFT; approve twice → 409; sale qty > balance → 422 INSUFFICIENT_STOCK; Purchase with Customer-only account → 422 ACCOUNT_TYPE_MISMATCH; cancel (admin) → reversal rows `isReversal=true`, balances restored; cancel by operator → 403; numbering sequential (SAT n, n+1).
11. Purchase: approve → stock In, account Credit, balances move accordingly.
12. Delete rules: stock with balance → 409 STOCK_HAS_BALANCE; stock in Draft → 409 STOCK_IN_USE; zero-balance unused stock → 204 then 404.
13. Reconciliation: run → 0 discrepancies; tamper `Stocks.Balance` via direct SQL → 1 discrepancy; `fix=true` → corrected.
14. Health endpoint 200; ProblemDetails contains `correlationId` and echoes `X-Correlation-Id`.

### 11.3 Postman (`postman/`)
`StockAccount.postman_collection.json` (v2.1) with folders Auth, Companies, Users, Stocks, CurrentAccounts, Invoices, Transactions, Portal, Admin, **Scenario – Ahmet Elma** (ordered requests with `pm.test` assertions). Collection-level pre-request script logs in when `accessToken` empty/expired and stores tokens; requests use `{{baseUrl}}`, `{{accessToken}}`; ids captured into collection variables. Environments `StockAccount.local.postman_environment.json` (baseUrl `http://localhost:5080/api`) and `StockAccount.docker.postman_environment.json` (same URL). `postman/package.json` script `npm test` runs newman against local env. Swagger JSON also available at `/swagger/v1/swagger.json`.

### 11.4 E2E (`e2e/`, Playwright, Chromium)
`playwright.config.ts` baseURL from `E2E_BASE_URL` (default `http://localhost:5173`); npm scripts `test:vue` (5173) and `test:nuxt` (3000). Requires running API with seed data. Each test creates uniquely-coded data (`E2E{timestamp}`) to be re-runnable.
1. Login invalid → `login-error` visible; admin login → dashboard (`page-title` "Gösterge Paneli").
2. Stock CRUD (admin, company switched to Demo): create → find via `stock-search` → edit name → delete (Evet) → gone.
3. Current account CRUD likewise.
4. **Ahmet/Apple UI flow** (demo operator): note ELMA `stock-balance` and C001 `account-balance` data-values; new invoice Sales, account C001, line 0 ELMA qty 2 price 50 VAT 1 → totals show data-value 100 / 1 / 101; save → status "Taslak"; approve (Evet) → status "Onaylandı" and `invoice-document-no` matches `/^SAT\d{6}$/`; ELMA balance decreased by 2; C001 balance increased by 101; Stok Hareketleri list shows the Çıkış row.
5. Cancel approved invoice as admin with reason → "İptal Edildi"; balances restored.
6. Portal: ahmet login → lands on `/my-account`, `my-balance` visible, `nav-stocks` absent; visiting `/stocks` → `/403`.
7. Operator: `nav-companies` absent; `/companies` → `/403`.
8. Paging/filter: stocks list with search "EL" shows ELMA row only; paginator changes page.
9. Token refresh: after login, set `sa.accessToken` in localStorage to an invalid/expired token, reload `/stocks` → page loads (refresh succeeded).
10. HTTP cache: visiting `/stocks` twice within TTL issues only one `GET /api/stocks` network request (count via `page.on('request')`); after creating a stock the list request is issued again.
