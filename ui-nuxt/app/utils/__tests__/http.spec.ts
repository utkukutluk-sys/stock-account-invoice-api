import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createApiClient, httpTiming } from '../http'
import { HttpCache } from '../cache'
import type { ApiError } from '../errors'

interface Call {
  url: string
  method: string
  headers: Record<string, string>
}
type Reply = { status: number; data?: unknown; headers?: Record<string, string> }

const calls: Call[] = []
let handler: (c: Call) => Reply
const sleeps: number[] = []

// Minimal `$fetch.raw` stand-in with ofetch semantics (throws FetchError-like objects on >= 400).
const raw = vi.fn(async (url: string, opts: { method?: string; headers?: Record<string, string> }) => {
  const call = { url, method: opts.method ?? 'GET', headers: opts.headers ?? {} }
  calls.push(call)
  const r = handler(call)
  const headers = new Headers(r.headers ?? {})
  const response = { status: r.status, ok: r.status < 400, _data: r.data, headers }
  if (r.status >= 400) throw Object.assign(new Error(`HTTP ${r.status}`), { status: r.status, data: r.data, response })
  return response
})
;(globalThis as unknown as { $fetch: unknown }).$fetch = Object.assign(vi.fn(), { raw })

function client(cache: HttpCache | null = null) {
  return createApiClient({
    baseURL: '/api',
    getAccessToken: () => 't',
    getExpiresAt: () => Date.now() + 600_000,
    isAdmin: () => true,
    getCompanyId: () => 3,
    refresh: () => Promise.reject(new Error('no')),
    cache,
  })
}

const limited = (retryAfter?: string): Reply => ({
  status: 429,
  data: { status: 429, code: 'RATE_LIMITED' },
  headers: retryAfter ? { 'Retry-After': retryAfter } : {},
})

beforeEach(() => {
  calls.length = 0
  sleeps.length = 0
  httpTiming.sleep = async (ms: number) => {
    sleeps.push(ms)
  }
})

describe('createApiClient (Nuxt)', () => {
  it('sends Authorization and X-Company-Id for admin', async () => {
    handler = () => ({ status: 200, data: {} })
    await client().get('/stocks')
    expect(calls[0]!.headers.Authorization).toBe('Bearer t')
    expect(calls[0]!.headers['X-Company-Id']).toBe('3')
  })

  it('retries a GET once after Retry-After on 429', async () => {
    let n = 0
    handler = () => (++n === 1 ? limited('2') : { status: 200, data: { ok: 1 } })
    expect(await client().get('/stocks')).toEqual({ ok: 1 })
    expect(sleeps).toEqual([2000])
    expect(calls).toHaveLength(2)
  })

  it('gives up after one retry with the Turkish message', async () => {
    handler = () => limited('5')
    const err = (await client().get('/stocks').catch((e: unknown) => e)) as ApiError
    expect(err.status).toBe(429)
    expect(err.displayMessage).toBe('Çok fazla istek gönderildi, 5 sn sonra tekrar deneyin.')
    expect(calls).toHaveLength(2)
  })

  it('never retries POST or login on 429', async () => {
    handler = () => limited('1')
    await expect(client().post('/auth/login', { email: 'a' })).rejects.toMatchObject({ status: 429 })
    await expect(client().post('/stocks', {})).rejects.toMatchObject({ status: 429 })
    expect(calls).toHaveLength(2)
    expect(sleeps).toEqual([])
  })

  it('does not cache 429/5xx responses', async () => {
    const cache = new HttpCache()
    let n = 0
    handler = () => {
      n++
      if (n <= 2) return limited('1')
      if (n === 3) return { status: 500, data: { status: 500 } }
      return { status: 200, data: { items: [] } }
    }
    const c = client(cache)
    await expect(c.get('/stocks')).rejects.toMatchObject({ status: 429 })
    await expect(c.get('/stocks')).rejects.toMatchObject({ status: 500 })
    expect(await c.get('/stocks')).toEqual({ items: [] })
    expect(await c.get('/stocks')).toEqual({ items: [] })
    expect(calls).toHaveLength(4)
  })
})
