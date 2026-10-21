// HTTP client: axios instance on same-origin `/api` with auth interceptors (CONTRACT §10.2)
// and the client cache (CONTRACT §10.3).
import axios, { AxiosHeaders, type AxiosInstance, type InternalAxiosRequestConfig } from 'axios'
import { ApiError, toApiError } from './errors'
import { HttpCache, buildCacheKey, resolveTtl, sortedQuery } from './cache'

declare module 'axios' {
  interface AxiosRequestConfig {
    /** Internal: request was already retried after a refresh. */
    _retry?: boolean
    /** Explicit X-Company-Id for this request (admin). `null` = send no header. */
    companyId?: number | null
  }
}

export interface HttpAuthHooks {
  getAccessToken(): string | null
  /** Expiry of the access token in epoch ms. */
  getExpiresAt(): number | null
  isAdmin(): boolean
  /** Admin's selected company (cookie `sa_company`). */
  getCompanyId(): number | null
  /** Single-flight refresh; resolves to the new access token, rejects when the session is gone. */
  refresh(): Promise<string>
  /** Called when a 401 could not be recovered: clear session and redirect to login. */
  onAuthFailure(): void
}

const noopHooks: HttpAuthHooks = {
  getAccessToken: () => null,
  getExpiresAt: () => null,
  isAdmin: () => false,
  getCompanyId: () => null,
  refresh: () => Promise.reject(new ApiError(401)),
  onAuthFailure: () => {},
}

let hooks: HttpAuthHooks = noopHooks

export function configureHttp(h: Partial<HttpAuthHooks>): void {
  hooks = { ...noopHooks, ...h }
}

export const httpCache = new HttpCache({ maxEntries: 200 })

const PROACTIVE_REFRESH_MS = 60_000

/** Any `/auth/*` endpoint: never retried after a 401 (CONTRACT §10.2). */
function isAuthUrl(url?: string): boolean {
  return !!url && /(^|\/)auth\//.test(url)
}

/** Anonymous cookie-based auth endpoints: no bearer header, no proactive refresh. */
function isSessionUrl(url?: string): boolean {
  return !!url && /(^|\/)auth\/(login|refresh|logout)/.test(url)
}

export function createHttp(): AxiosInstance {
  const instance = axios.create({
    baseURL: '/api',
    withCredentials: true,
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
  })

  instance.interceptors.request.use(async (config: InternalAxiosRequestConfig) => {
    const headers = AxiosHeaders.from(config.headers)
    if (!isSessionUrl(config.url)) {
      let token = hooks.getAccessToken()
      const exp = hooks.getExpiresAt()
      if (token && exp !== null && exp - Date.now() < PROACTIVE_REFRESH_MS) {
        try {
          token = await hooks.refresh()
        } catch {
          // fall through; a 401 will trigger the regular recovery path
        }
      }
      if (token) headers.set('Authorization', `Bearer ${token}`)
      if (hooks.isAdmin()) {
        const companyId = config.companyId !== undefined ? config.companyId : hooks.getCompanyId()
        if (companyId) headers.set('X-Company-Id', String(companyId))
      }
    }
    config.headers = headers
    return config
  })

  instance.interceptors.response.use(
    (res) => res,
    async (error: unknown) => {
      const e = error as { config?: InternalAxiosRequestConfig; response?: { status: number } }
      const config = e.config
      if (e.response?.status === 401 && config && !config._retry && !isAuthUrl(config.url)) {
        config._retry = true
        let token: string
        try {
          token = await hooks.refresh()
        } catch {
          hooks.onAuthFailure()
          throw toApiError(error)
        }
        const headers = AxiosHeaders.from(config.headers)
        headers.set('Authorization', `Bearer ${token}`)
        config.headers = headers
        return instance.request(config)
      }
      throw toApiError(error)
    },
  )

  return instance
}

export const http = createHttp()

export interface GetOptions {
  /** Bypass a fresh cache entry ("Yenile" button). */
  force?: boolean
  /** AutoComplete lookup: cached for 300 s. */
  lookup?: boolean
  /** Disable client caching for this call. */
  noCache?: boolean
  /** Explicit X-Company-Id (admin). `null` = no header. */
  companyId?: number | null
}

function cleanParams(params?: Record<string, unknown> | null): Record<string, unknown> | undefined {
  if (!params) return undefined
  const out: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== '') out[k] = v
  }
  return out
}

function effectiveCompanyId(opts: GetOptions): number | null {
  if (!hooks.isAdmin()) return null
  return opts.companyId !== undefined ? opts.companyId : hooks.getCompanyId()
}

/** Cached GET. `url` is relative to `/api`, e.g. `/stocks`. */
export async function get<T>(url: string, params?: Record<string, unknown> | null, opts: GetOptions = {}): Promise<T> {
  const p = cleanParams(params)
  const companyId = effectiveCompanyId(opts)
  const key = buildCacheKey('GET', url, p, companyId)
  const qs = sortedQuery(p)
  const ttl = opts.noCache ? 0 : resolveTtl(qs ? `${url}?${qs}` : url, { lookup: opts.lookup })
  return httpCache.get<T>(
    key,
    ttl,
    async (etag) => {
      const res = await http.get<T>(url, {
        params: p,
        headers: etag ? { 'If-None-Match': etag } : undefined,
        validateStatus: (s) => (s >= 200 && s < 300) || s === 304,
        companyId: opts.companyId,
      })
      const tag = res.headers?.['etag']
      return { status: res.status, data: res.data, etag: typeof tag === 'string' ? tag : null }
    },
    { force: opts.force },
  )
}

export interface MutateOptions {
  /** Prefixes invalidated after success (CONTRACT §10.3). */
  invalidate?: readonly string[]
  companyId?: number | null
}

async function mutate<T>(method: 'post' | 'put' | 'delete', url: string, body: unknown, opts: MutateOptions): Promise<T> {
  const res = await http.request<T>({ method, url, data: body ?? (method === 'delete' ? undefined : {}), companyId: opts.companyId })
  if (opts.invalidate?.length) httpCache.invalidate([...opts.invalidate])
  return res.data
}

export const post = <T>(url: string, body?: unknown, opts: MutateOptions = {}) => mutate<T>('post', url, body, opts)
export const put = <T>(url: string, body?: unknown, opts: MutateOptions = {}) => mutate<T>('put', url, body, opts)
export const del = (url: string, opts: MutateOptions = {}) => mutate<void>('delete', url, undefined, opts)
