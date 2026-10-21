// Client HTTP cache (CONTRACT §10.3). Transport-agnostic so it can be unit-tested
// and reused by the Nuxt port: the caller supplies a `fetcher` that performs the request.

export interface CacheEntry<T = unknown> {
  data: T
  etag: string | null
  expiresAt: number
}

export interface FetchResult<T = unknown> {
  status: number
  data: T
  etag: string | null
}

/** Performs the network request; `etag` is the cached ETag to send as If-None-Match (may be null). */
export type Fetcher<T> = (etag: string | null) => Promise<FetchResult<T>>

export interface CacheGetOptions {
  /** Bypass a fresh entry and go to the network (the ETag is still sent for cheap revalidation). */
  force?: boolean
}

export interface HttpCacheOptions {
  maxEntries?: number
  now?: () => number
}

const SECOND = 1000

/**
 * TTL (ms) by URL. `path` is relative to the API base (e.g. `/stocks?page=1`).
 * 0 = never cached.
 */
export function resolveTtl(path: string, opts: { lookup?: boolean } = {}): number {
  const p = path.split('?')[0] ?? ''
  if (p.startsWith('/auth')) return 0
  // AutoComplete lookups (pageSize=10, flagged by the caller) are long-lived.
  if (opts.lookup) return 300 * SECOND
  if (/^\/[^/]+\/\d+\/transactions/.test(p)) return 30 * SECOND
  if (p.startsWith('/stocks') || p.startsWith('/current-accounts')) return 60 * SECOND
  if (p.startsWith('/companies')) return 300 * SECOND
  if (p.startsWith('/invoices')) return 30 * SECOND
  if (p.startsWith('/stock-trans') || p.startsWith('/act-trans')) return 30 * SECOND
  if (p.startsWith('/dashboard')) return 30 * SECOND
  if (p.startsWith('/me')) return 30 * SECOND
  if (p.startsWith('/users')) return 30 * SECOND
  return 0
}

/** Serializes params into a query string with keys sorted; empty values are dropped. */
export function sortedQuery(params?: Record<string, unknown> | null): string {
  if (!params) return ''
  const parts = Object.keys(params)
    .filter((k) => params[k] !== undefined && params[k] !== null && params[k] !== '')
    .sort()
    .map((k) => `${encodeURIComponent(k)}=${encodeURIComponent(String(params[k]))}`)
  return parts.join('&')
}

/** Cache key = method + path with sorted query + company id (X-Company-Id). */
export function buildCacheKey(
  method: string,
  path: string,
  params?: Record<string, unknown> | null,
  companyId?: number | string | null,
): string {
  const [base, existing] = path.split('?')
  const merged: Record<string, unknown> = {}
  if (existing) new URLSearchParams(existing).forEach((v, k) => (merged[k] = v))
  Object.assign(merged, params ?? {})
  const qs = sortedQuery(merged)
  return `${method.toUpperCase()} ${base}${qs ? `?${qs}` : ''} #c=${companyId ?? ''}`
}

/** Extracts the path part (`/stocks?...`) of a cache key, used for prefix invalidation. */
function keyPath(key: string): string {
  const start = key.indexOf(' ') + 1
  const end = key.lastIndexOf(' #c=')
  return key.slice(start, end < 0 ? undefined : end)
}

export class HttpCache {
  private readonly entries = new Map<string, CacheEntry>()
  private readonly inflight = new Map<string, Promise<unknown>>()
  private readonly maxEntries: number
  private readonly now: () => number
  /** Incremented on every invalidate/clear so in-flight responses started earlier are not stored. */
  private generation = 0

  constructor(opts: HttpCacheOptions = {}) {
    this.maxEntries = opts.maxEntries ?? 200
    this.now = opts.now ?? (() => Date.now())
  }

  get size(): number {
    return this.entries.size
  }

  /** Returns the entry if present (fresh or stale) and marks it most-recently-used. */
  peek<T>(key: string): CacheEntry<T> | undefined {
    const e = this.entries.get(key)
    if (!e) return undefined
    this.entries.delete(key)
    this.entries.set(key, e)
    return e as CacheEntry<T>
  }

  /** Returns data only when the entry is still fresh. */
  getFresh<T>(key: string): T | undefined {
    const e = this.entries.get(key)
    if (e && this.now() < e.expiresAt) return this.peek<T>(key)!.data
    return undefined
  }

  set<T>(key: string, data: T, etag: string | null, ttlMs: number): void {
    if (this.entries.has(key)) this.entries.delete(key)
    this.entries.set(key, { data, etag, expiresAt: this.now() + ttlMs })
    while (this.entries.size > this.maxEntries) {
      const oldest = this.entries.keys().next().value
      if (oldest === undefined) break
      this.entries.delete(oldest)
    }
  }

  /**
   * Cached GET: fresh entry → no network; stale entry with ETag → conditional request, 304 reuses data;
   * identical concurrent requests share one promise.
   */
  async get<T>(key: string, ttlMs: number, fetcher: Fetcher<T>, opts: CacheGetOptions = {}): Promise<T> {
    if (ttlMs <= 0) return (await fetcher(null)).data

    if (!opts.force) {
      const fresh = this.getFresh<T>(key)
      if (fresh !== undefined) return fresh
    }

    const pending = this.inflight.get(key)
    if (pending) return pending as Promise<T>

    const generation = this.generation
    const run = (async () => {
      const cached = this.entries.get(key) as CacheEntry<T> | undefined
      let res = await fetcher(cached?.etag ?? null)
      if (res.status === 304) {
        const current = this.entries.get(key) as CacheEntry<T> | undefined
        if (current) {
          if (generation === this.generation) this.set(key, current.data, current.etag, ttlMs)
          return current.data
        }
        // Entry was evicted/invalidated meanwhile: refetch unconditionally.
        res = await fetcher(null)
      }
      if (generation === this.generation) this.set(key, res.data, res.etag, ttlMs)
      return res.data
    })()

    this.inflight.set(key, run)
    try {
      return await run
    } finally {
      if (this.inflight.get(key) === run) this.inflight.delete(key)
    }
  }

  /** Removes every entry whose path starts with one of the prefixes (e.g. `/stocks`). */
  invalidate(prefixes: string[]): void {
    if (prefixes.length === 0) return
    this.generation++
    for (const key of [...this.entries.keys()]) {
      const path = keyPath(key)
      if (prefixes.some((p) => path === p || path.startsWith(`${p}/`) || path.startsWith(`${p}?`))) {
        this.entries.delete(key)
      }
    }
    for (const key of [...this.inflight.keys()]) {
      const path = keyPath(key)
      if (prefixes.some((p) => path === p || path.startsWith(`${p}/`) || path.startsWith(`${p}?`))) {
        this.inflight.delete(key)
      }
    }
  }

  clear(): void {
    this.generation++
    this.entries.clear()
    this.inflight.clear()
  }
}

/** Invalidation map (CONTRACT §10.3). */
export const InvalidationMap = {
  stock: ['/stocks', '/dashboard'],
  currentAccount: ['/current-accounts', '/dashboard'],
  invoice: ['/invoices', '/dashboard'],
  invoiceStatus: ['/invoices', '/stocks', '/current-accounts', '/stock-trans', '/act-trans', '/dashboard', '/me'],
  company: ['/companies', '/stocks', '/current-accounts', '/invoices'],
  user: ['/users'],
} as const satisfies Record<string, readonly string[]>
