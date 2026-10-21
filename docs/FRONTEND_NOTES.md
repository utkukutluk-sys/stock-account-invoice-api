# Frontend notes (ui-vue, ui-nuxt)

Binding spec: `docs/CONTRACT.md` §8, §10. Both UIs have the same routes, Turkish texts and `data-testid`s.

## How to run

| | Dev | Prod / Docker |
|---|---|---|
| ui-vue | `cd ui-vue && npm ci && npm run dev` → http://localhost:5173 (Vite proxies `/api` → `VITE_PROXY_TARGET`, default `http://localhost:5080`) | `docker build -t sa-ui-vue ui-vue` (node build → nginx:1.27; `/api/` proxied to `http://api:8080`; SPA fallback; `/healthz`) |
| ui-nuxt | `cd ui-nuxt && npm ci && npm run dev` → http://localhost:3000 (SSR; Nitro route proxies `/api` → `NUXT_API_BASE`, default `http://localhost:5080`) | `npm run build && node .output/server/index.mjs`; `docker build -t sa-ui-nuxt ui-nuxt` (node:24-alpine runtime, port 3000, `NUXT_API_BASE=http://api:8080`) |

Scripts: ui-vue `npm run build` (vue-tsc + vite), `npm test` (Vitest, 74 tests), `npm run lint` (ESLint + vue-tsc).
ui-nuxt `npm run build`, `npm test` (Vitest, 53 tests), `npx vue-tsc --build` (type check).

## Structure

ui-vue/src: `api/` (`http.ts` axios + interceptors, `cache.ts`, `refresh.ts`, `errors.ts`, one module per resource), `stores/` (auth, ui), `router/`, `layouts/AppLayout.vue`, `views/`, `components/` (DataTable helpers, transactions tables, dialogs), `composables/` (`useLazyList`, `useFormErrors`, `useNotify`, `useCompanyGuard`), `utils/` (`money.ts`, `format.ts`, `validation.ts`, `invoiceForm.ts`), `i18n/` (labels, PrimeVue tr locale), `types/api.ts`.
ui-nuxt/app: same pieces; `utils/http.ts` (`createApiClient`, shared by SSR and browser) and `utils/cache.ts`; other logic in `lib/`; `plugins/api.ts`, `plugins/auth.server.ts`, `middleware/auth.global.ts`, `composables/useCachedAsyncData.ts`; `server/routes/api/[...path].ts` (proxy), `server/utils/ssrAuth.ts`.

PrimeVue 4.5 (Aura, emerald primary, `.app-dark` dark mode toggle), PrimeFlex 4, primeicons. ui-vue uses unplugin-vue-components with PrimeVueResolver (tree-shaken), lazy route chunks and manual vendor chunks. ui-nuxt uses `@primevue/nuxt-module` with auto-import.

## Auth
- The access token, its expiry and the user are held in memory only: the Pinia store, backed by `useState('auth')` in Nuxt. Nothing is written to web storage.
- The refresh token is the httpOnly `sa_refresh` cookie. `/auth/refresh` and `/auth/logout` send `{}` as `application/json`.
- Bootstrap runs a silent refresh before the first guarded navigation. Refresh is single-flight: concurrent 401s share one refresh and are each retried once. A 401 on refresh is retried once after 500 ms (grace window). A proactive refresh runs when the token has less than 60 s left. If refresh fails, the store and cache are cleared and the user goes to `/login?redirect=`.
- Admin company: stored in the `sa_company` cookie for UI persistence. Every admin request sends it as the **`X-Company-Id` header**, including browser requests, Nuxt SSR requests and requests through the Nitro proxy. The API only reads the header.
- Nuxt SSR:
  - `auth.server.ts` calls `ssrRefresh` once per request (memoized on `event.context`), forwarding the cookie and `X-Forwarded-For`.
  - It passes the API's `Set-Cookie` through to the browser.
  - The guard redirects anonymous users with a 302.
  - Until hydration, the layout keeps form controls inside a `<fieldset disabled>`, so input typed before hydration is not lost and Playwright waits for the controls to become enabled.

## HTTP cache design (CONTRACT §10.3)
- `HttpCache` is transport-agnostic and unit-tested. It is a `Map` with LRU eviction (200 entries).
- Key = `GET` + path with sorted query + `#c=<X-Company-Id>`.
- A fresh entry is returned with no network call. A stale entry with an ETag is revalidated with `If-None-Match`; on 304 the cached data is reused and its expiry renewed.
- Identical in-flight GETs share one promise.
- `invalidate(prefixes)` matches path segments, so `/stocks` does not match `/stock-trans`. A generation counter keeps responses that started before an invalidation out of the cache.
- TTL by prefix:

  | Requests | TTL |
  |---|---|
  | AutoComplete lookups (`lookup: true`) | 300 s |
  | `/companies` | 300 s |
  | `/stocks`, `/current-accounts` | 60 s |
  | `/*/{id}/transactions`, `/invoices`, `/stock-trans`, `/act-trans`, `/dashboard`, `/me`, `/users` | 30 s |
  | `/auth` | never cached |

- Mutations use the invalidation map from the contract. Login, logout and company switch clear the whole cache. The "Yenile" button uses `force`.
- Nuxt: `useAsyncData` keys equal the cache keys. `getCachedData` returns payload data while hydrating and also seeds the client cache with it; after that it returns a fresh cache entry. SSR requests use no cache.

## Notes for E2E (Test agent)
- Composite components (Select/AutoComplete/InputNumber/DatePicker/ToggleSwitch) sit in a `data-testid` wrapper div and fill its width. Click the wrapper to open them.
- VAT options are labelled `%0 %1 %10 %20`. Selecting a stock pre-fills the price and VAT.
- Extra test ids:
  - `line-{i}-net`, `line-{i}-vat`, `line-{i}-description`
  - `dash-approved-count`, `dash-purchase-total`, `dash-payable`
  - `*-filter-*` for transaction tables
  - `my-invoices-refresh`
  - profile: `profile-*`
  - stock/account card "Hareketler" tabs: tables `stock-tab-trans-table`, `account-tab-trans-table`
- `invoice-status` contains the Tag text (`Taslak`/`Onaylandı`/`İptal Edildi`, or `Yeni` before the first save). `invoice-document-no` contains only the document number, or `—`.
- Approve is shown only for a saved Draft. If the form has unsaved changes, it saves them first.

## Deviations / decisions
- ui-nuxt uses `$fetch`/ofetch, not axios (§10.7 asks for `$fetch.create`). Nuxt 4.6.2 with `@primevue/nuxt-module` 4.5.5.
- ui-vue uses Vite 8, Vitest 5 and pinia 3. ui-nuxt uses pinia 4, as required by `@pinia/nuxt` 1.x.
- The theme preference is stored in localStorage `sa_theme` in ui-vue and in the cookie `sa_theme` in ui-nuxt, so SSR renders without a theme flash. It is a UI preference only, not auth data.
- `npm audit` reports high-severity issues in dev-only lint tooling (braces via @vue/eslint-config-typescript). None are in runtime dependencies.

## Requests to Backend
- None blocking. Both UIs were verified end to end against the real API on 5080 on 2026-10-22 (16/16 smoke steps each).
- The UIs already follow BACKEND_NOTES §4: the `X-Company-Id` header, a JSON body on refresh/logout, and no `quantity` sort on ActTrans.

## Not verified
- Docker images were not built: there is no Docker CLI on this machine. The Dockerfiles and nginx.conf were written to match the contract.
