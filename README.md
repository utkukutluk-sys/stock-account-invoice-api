# StockAccountApp

Stock / Current Account (Cari) / Invoice Web API (.NET 10, EF Core 10, SQL Server, Redis, Serilog) with two UIs
(Vue 3 + PrimeVue in `ui-vue/`, Nuxt 4 SSR port in `ui-nuxt/`). Binding spec: `docs/CONTRACT.md`.

## Prerequisites
- .NET SDK 10, SQL Server Express `.\SQLEXPRESS` (Windows auth), Node 24 (UIs), Docker Desktop (optional: Redis, full stack).

## Run locally
```powershell
docker run -d --name sa-redis -p 6379:6379 redis:7-alpine   # optional; without it the API uses an in-memory cache
dotnet tool restore
dotnet run --project src/StockAccount.Api                    # http://localhost:5080, migrates + seeds on first start
```
- Swagger: http://localhost:5080/swagger · Health: http://localhost:5080/health
- UIs: `cd ui-vue; npm ci; npm run dev` (http://localhost:5173) · `cd ui-nuxt; npm ci; npm run dev` (http://localhost:3000).
  Both proxy `/api` to the API.

## Run with Docker
```powershell
copy .env.example .env
docker stop sa-redis        # compose redis also binds 6379
docker compose up --build -d
```
API http://localhost:5080, ui-vue http://localhost:5173, ui-nuxt http://localhost:3000, SQL Server localhost,14333 (sa).

## Seed users
| Email | Password | Role |
|---|---|---|
| admin@stockaccount.local | Admin123! | Admin (all companies; `X-Company-Id` selects one) |
| demo@stockaccount.local | Demo123! | Operator of "Demo Gıda A.Ş." |
| ahmet@stockaccount.local | Ahmet123! | Portal user of account C001 (Ahmet Yılmaz) |

## Tests
- Unit: `dotnet test tests/StockAccount.UnitTests`
- Integration: `dotnet test tests/StockAccount.IntegrationTests` (SQLEXPRESS, per-run database)
- Postman: `cd postman; npm test` · E2E: `cd e2e; npm run test:vue` / `npm run test:nuxt`

## Architecture (summary)
Clean architecture: `Domain` (entities, calculations) ← `Application` (services, DTOs, FluentValidation, cache/auth
abstractions) ← `Infrastructure` (EF Core, Identity/JWT, Redis cache with fallback, reconciliation background service)
← `Api` (controllers, ProblemDetails, ETag, correlation id, Serilog, Swagger, health). Key decisions: JWT access token in
memory + rotating refresh token in an httpOnly `sa_refresh` cookie, immutable ledgers (StockTrans/ActTrans) written only by
invoice approve/cancel in one transaction with optimistic-concurrency retry, stored balances verified by a periodic
reconciliation job. Details: `docs/BACKEND_NOTES.md`, `docs/DB_NOTES.md`.
