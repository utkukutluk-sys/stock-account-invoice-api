import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AxiosAdapter, AxiosResponse, InternalAxiosRequestConfig } from 'axios'
import { AxiosError, AxiosHeaders } from 'axios'
import { configureHttp, get, http, httpCache, post } from '../http'
import { ApiError } from '../errors'
import { createRefreshController } from '../refresh'

type Handler = (config: InternalAxiosRequestConfig) => { status: number; data?: unknown; headers?: Record<string, string> }

let handler: Handler
const calls: InternalAxiosRequestConfig[] = []

const adapter: AxiosAdapter = async (config) => {
  calls.push(config)
  const r = handler(config)
  const response: AxiosResponse = {
    status: r.status,
    statusText: String(r.status),
    data: r.data ?? '',
    headers: r.headers ?? {},
    config,
  }
  const validate = config.validateStatus ?? ((s: number) => s >= 200 && s < 300)
  if (validate(r.status)) return response
  throw new AxiosError(`HTTP ${r.status}`, undefined, config, undefined, response)
}

const header = (c: InternalAxiosRequestConfig, name: string) => AxiosHeaders.from(c.headers).get(name)

/** Minimal in-memory session wired like main.ts does with the Pinia store. */
function session(opts: { token?: string | null; expiresInMs?: number; admin?: boolean; company?: number | null } = {}) {
  const state = {
    token: opts.token === undefined ? 'old' : opts.token,
    expiresAt: Date.now() + (opts.expiresInMs ?? 10 * 60_000),
    failures: 0,
  }
  const refreshes = vi.fn(async () => {
    const res = await http.post<{ accessToken: string }>('/auth/refresh', {})
    return res.data.accessToken
  })
  const controller = createRefreshController({ doRefresh: refreshes, sleep: () => Promise.resolve() })
  configureHttp({
    getAccessToken: () => state.token,
    getExpiresAt: () => state.expiresAt,
    isAdmin: () => !!opts.admin,
    getCompanyId: () => opts.company ?? null,
    refresh: async () => {
      try {
        const t = await controller.refresh()
        state.token = t
        state.expiresAt = Date.now() + 15 * 60_000
        return t
      } catch (e) {
        state.token = null
        throw e
      }
    },
    onAuthFailure: () => {
      state.failures++
    },
  })
  return { state, refreshes }
}

beforeEach(() => {
  http.defaults.adapter = adapter
  calls.length = 0
  httpCache.clear()
})

afterEach(() => {
  configureHttp({})
})

describe('http auth interceptors', () => {
  it('adds the bearer token and X-Company-Id for admins', async () => {
    session({ admin: true, company: 7 })
    handler = () => ({ status: 200, data: { ok: true } })
    await get('/stocks', { page: 1 }, { noCache: true })
    expect(header(calls[0]!, 'Authorization')).toBe('Bearer old')
    expect(header(calls[0]!, 'X-Company-Id')).toBe('7')
  })

  it('never sends X-Company-Id for non-admins', async () => {
    session({ admin: false, company: 7 })
    handler = () => ({ status: 200, data: {} })
    await get('/stocks', null, { noCache: true })
    expect(header(calls[0]!, 'X-Company-Id')).toBeFalsy()
  })

  it('an explicit companyId overrides the selected company', async () => {
    session({ admin: true, company: 7 })
    handler = () => ({ status: 200, data: { items: [] } })
    await get('/current-accounts', { q: 'a' }, { noCache: true, companyId: 9 })
    expect(header(calls[0]!, 'X-Company-Id')).toBe('9')
  })

  it('on 401 refreshes once (single flight) and retries all queued requests with the new token', async () => {
    const { refreshes } = session()
    handler = (c) => {
      if (c.url === '/auth/refresh') return { status: 200, data: { accessToken: 'new' } }
      return header(c, 'Authorization') === 'Bearer new' ? { status: 200, data: { url: c.url } } : { status: 401 }
    }
    const results = await Promise.all([
      get<{ url: string }>('/stocks', null, { noCache: true }),
      get<{ url: string }>('/current-accounts', null, { noCache: true }),
      get<{ url: string }>('/invoices', null, { noCache: true }),
    ])
    expect(results.map((r) => r.url)).toEqual(['/stocks', '/current-accounts', '/invoices'])
    expect(refreshes).toHaveBeenCalledTimes(1)
    expect(calls.filter((c) => c.url === '/auth/refresh')).toHaveLength(1)
  })

  it('when refresh fails it reports an auth failure and rejects with ApiError 401', async () => {
    const s = session()
    handler = () => ({ status: 401, data: { code: 'INVALID_REFRESH_TOKEN', detail: 'Oturum sona erdi.' } })
    await expect(get('/stocks', null, { noCache: true })).rejects.toMatchObject({ status: 401 })
    expect(s.state.failures).toBe(1)
    // the 401 of /auth/refresh itself is never retried through the interceptor
    expect(calls.filter((c) => c.url === '/auth/refresh').length).toBe(2) // first try + single grace retry
  })

  it('does not retry /auth/* endpoints after a 401', async () => {
    const s = session({ token: null })
    handler = () => ({ status: 401, data: { code: 'INVALID_CREDENTIALS' } })
    await expect(http.post('/auth/login', { email: 'a', password: 'b' })).rejects.toBeInstanceOf(ApiError)
    expect(calls).toHaveLength(1)
    expect(s.state.failures).toBe(0)
  })

  it('refreshes proactively when the token expires within 60 s', async () => {
    const { refreshes } = session({ expiresInMs: 30_000 })
    handler = (c) =>
      c.url === '/auth/refresh' ? { status: 200, data: { accessToken: 'fresh' } } : { status: 200, data: {} }
    await get('/dashboard/summary', null, { noCache: true })
    expect(refreshes).toHaveBeenCalledTimes(1)
    const apiCall = calls.find((c) => c.url === '/dashboard/summary')!
    expect(header(apiCall, 'Authorization')).toBe('Bearer fresh')
  })

  it('maps ProblemDetails to ApiError with normalized validation keys', async () => {
    session()
    handler = () => ({
      status: 400,
      data: { status: 400, code: 'VALIDATION_FAILED', errors: { 'Lines[0].Quantity': ['Miktar 0dan büyük olmalıdır.'] } },
    })
    const err = (await post('/invoices', {}).catch((e: unknown) => e)) as ApiError
    expect(err).toBeInstanceOf(ApiError)
    expect(err.code).toBe('VALIDATION_FAILED')
    expect(err.errors['lines[0].quantity']).toEqual(['Miktar 0dan büyük olmalıdır.'])
  })
})

describe('cached GET over http', () => {
  it('serves repeated GETs from cache and revalidates with ETag after expiry', async () => {
    session()
    vi.useFakeTimers({ toFake: ['Date'] })
    try {
      let version = 'W/"1"'
      handler = (c) => {
        if (header(c, 'If-None-Match') === version) return { status: 304 }
        return { status: 200, data: { items: [1] }, headers: { etag: version } }
      }
      await get('/stocks', { page: 1 })
      await get('/stocks', { page: 1 })
      expect(calls).toHaveLength(1)
      vi.setSystemTime(Date.now() + 61_000)
      expect(await get('/stocks', { page: 1 })).toEqual({ items: [1] })
      expect(calls).toHaveLength(2)
      expect(header(calls[1]!, 'If-None-Match')).toBe('W/"1"')
      version = 'W/"2"'
    } finally {
      vi.useRealTimers()
    }
  })

  it('mutations invalidate the configured prefixes', async () => {
    session()
    handler = (c) => ({ status: c.method === 'post' ? 201 : 200, data: { url: c.url } })
    await get('/stocks', { page: 1 })
    await post('/stocks', { code: 'X' }, { invalidate: ['/stocks', '/dashboard'] })
    await get('/stocks', { page: 1 })
    expect(calls.filter((c) => c.method === 'get')).toHaveLength(2)
  })
})
