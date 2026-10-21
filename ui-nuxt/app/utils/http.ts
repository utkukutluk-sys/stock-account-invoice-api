// API client shared by SSR and browser (CONTRACT §10.2, §10.3, §10.7).
// - Server: targets `apiBase + '/api'` with the SSR access token; no cache (each SSR request starts empty).
// - Browser: targets same-origin `/api` with the client HTTP cache, proactive + single-flight refresh on 401.
import { toApiError } from './errors'
import { HttpCache, buildCacheKey, resolveTtl, sortedQuery } from './cache'

export interface ApiClientOptions {
  baseURL: string
  getAccessToken(): string | null
  /** Access token expiry in epoch ms. */
  getExpiresAt(): number | null
  isAdmin(): boolean
  /** Admin's selected company (cookie `sa_company`). */
  getCompanyId(): number | null
  /** Single-flight refresh resolving to the new access token (browser only). */
  refresh?: () => Promise<string>
  /** Unrecoverable 401: clear session and go to login (browser only). */
  onAuthFailure?: () => void
  /** Client cache (browser only). */
  cache?: HttpCache | null
  /** Extra headers on every request (SSR: X-Forwarded-For). */
  headers?: Record<string, string>
}

export interface GetOptions {
  force?: boolean
  lookup?: boolean
  noCache?: boolean
  companyId?: number | null
}

export interface MutateOptions {
  invalidate?: readonly string[]
  companyId?: number | null
}

interface RawOptions {
  params?: Record<string, unknown>
  body?: unknown
  headers?: Record<string, string>
  companyId?: number | null
  retry?: boolean
}

const PROACTIVE_REFRESH_MS = 60_000
const isAuthUrl = (url: string) => /(^|\/)auth\//.test(url)
const isSessionUrl = (url: string) => /(^|\/)auth\/(login|refresh|logout)/.test(url)

export function cleanParams(params?: Record<string, unknown> | null): Record<string, unknown> | undefined {
  if (!params) return undefined
  const out: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') out[k] = v
  return out
}

/** One browser-wide cache instance (never used on the server). */
export const httpCache = new HttpCache({ maxEntries: 200 })

export type ApiClient = ReturnType<typeof createApiClient>

export function createApiClient(o: ApiClientOptions) {
  function companyFor(explicit: number | null | undefined): number | null {
    if (!o.isAdmin()) return null
    return explicit !== undefined ? explicit : o.getCompanyId()
  }

  async function raw<T>(method: string, url: string, opts: RawOptions = {}): Promise<{ status: number; data: T; etag: string | null }> {
    const headers: Record<string, string> = { Accept: 'application/json', ...o.headers, ...opts.headers }
    if (!isSessionUrl(url)) {
      let token = o.getAccessToken()
      const exp = o.getExpiresAt()
      if (o.refresh && token && exp !== null && exp - Date.now() < PROACTIVE_REFRESH_MS) {
        try {
          token = await o.refresh()
        } catch {
          // the 401 path below recovers or fails the session
        }
      }
      if (token) headers.Authorization = `Bearer ${token}`
      const company = companyFor(opts.companyId)
      if (company) headers['X-Company-Id'] = String(company)
    }
    try {
      const res = await $fetch.raw<T>(url, {
        baseURL: o.baseURL,
        method: method as 'GET',
        query: opts.params,
        body: opts.body as Record<string, unknown> | undefined,
        headers,
        credentials: 'include',
      })
      return { status: res.status, data: res._data as T, etag: res.headers.get('etag') }
    } catch (err) {
      const e = toApiError(err)
      if (e.status === 401 && opts.retry !== false && !isAuthUrl(url) && o.refresh) {
        try {
          await o.refresh()
        } catch {
          o.onAuthFailure?.()
          throw e
        }
        return raw<T>(method, url, { ...opts, retry: false })
      }
      throw e
    }
  }

  async function get<T>(url: string, params?: Record<string, unknown> | null, opts: GetOptions = {}): Promise<T> {
    const p = cleanParams(params)
    if (!o.cache || opts.noCache) return (await raw<T>('GET', url, { params: p, companyId: opts.companyId })).data
    const key = buildCacheKey('GET', url, p, companyFor(opts.companyId))
    const qs = sortedQuery(p)
    const ttl = resolveTtl(qs ? `${url}?${qs}` : url, { lookup: opts.lookup })
    return o.cache.get<T>(
      key,
      ttl,
      (etag) => raw<T>('GET', url, { params: p, companyId: opts.companyId, headers: etag ? { 'If-None-Match': etag } : undefined }),
      { force: opts.force },
    )
  }

  async function mutate<T>(method: 'POST' | 'PUT' | 'DELETE', url: string, body: unknown, opts: MutateOptions = {}): Promise<T> {
    const res = await raw<T>(method, url, {
      body: method === 'DELETE' ? undefined : (body ?? {}),
      companyId: opts.companyId,
    })
    if (opts.invalidate?.length) o.cache?.invalidate([...opts.invalidate])
    return res.data
  }

  return {
    get,
    post: <T>(url: string, body?: unknown, opts?: MutateOptions) => mutate<T>('POST', url, body, opts),
    put: <T>(url: string, body?: unknown, opts?: MutateOptions) => mutate<T>('PUT', url, body, opts),
    del: (url: string, opts?: MutateOptions) => mutate<void>('DELETE', url, undefined, opts),
    /** Cache key used for both the client cache and the useAsyncData key (CONTRACT §10.7). */
    keyFor: (url: string, params?: Record<string, unknown> | null, companyId?: number | null) =>
      buildCacheKey('GET', url, cleanParams(params), companyFor(companyId)),
    cache: o.cache ?? null,
  }
}
