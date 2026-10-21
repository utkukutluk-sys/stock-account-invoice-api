import { describe, expect, it, vi } from 'vitest'
import { HttpCache, buildCacheKey, resolveTtl, sortedQuery, type FetchResult } from '../cache'

function clock(start = 1_000_000) {
  let t = start
  return { now: () => t, advance: (ms: number) => (t += ms) }
}

function ok<T>(data: T, etag: string | null = 'W/"a"'): FetchResult<T> {
  return { status: 200, data, etag }
}

describe('cache keys', () => {
  it('sorts query params and drops empty values', () => {
    expect(sortedQuery({ q: 'el', page: 1, sort: '', b: null, a: undefined })).toBe('page=1&q=el')
  })

  it('same params in a different order produce the same key', () => {
    const a = buildCacheKey('get', '/stocks', { page: 1, q: 'EL', pageSize: 20 }, 5)
    const b = buildCacheKey('GET', '/stocks?pageSize=20', { q: 'EL', page: 1 }, 5)
    expect(a).toBe(b)
  })

  it('includes the company id', () => {
    expect(buildCacheKey('GET', '/stocks', {}, 1)).not.toBe(buildCacheKey('GET', '/stocks', {}, 2))
  })
})

describe('resolveTtl', () => {
  it.each([
    ['/auth/me', 0],
    ['/stocks?page=1', 60_000],
    ['/stocks/5', 60_000],
    ['/current-accounts', 60_000],
    ['/stocks/5/transactions', 30_000],
    ['/current-accounts/5/transactions?page=1', 30_000],
    ['/companies', 300_000],
    ['/invoices/1', 30_000],
    ['/stock-trans', 30_000],
    ['/act-trans', 30_000],
    ['/dashboard/summary', 30_000],
    ['/me/account', 30_000],
  ])('%s → %d ms', (path, ttl) => {
    expect(resolveTtl(path)).toBe(ttl)
  })

  it('AutoComplete lookups live 300 s', () => {
    expect(resolveTtl('/stocks?pageSize=10&q=el', { lookup: true })).toBe(300_000)
  })
})

describe('HttpCache', () => {
  it('returns fresh data without a network call', async () => {
    const c = clock()
    const cache = new HttpCache({ now: c.now })
    const fetcher = vi.fn().mockResolvedValue(ok({ n: 1 }))
    expect(await cache.get('k', 1000, fetcher)).toEqual({ n: 1 })
    c.advance(999)
    expect(await cache.get('k', 1000, fetcher)).toEqual({ n: 1 })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('revalidates a stale entry with If-None-Match and reuses data on 304', async () => {
    const c = clock()
    const cache = new HttpCache({ now: c.now })
    const fetcher = vi
      .fn()
      .mockResolvedValueOnce(ok({ n: 1 }, 'W/"v1"'))
      .mockResolvedValueOnce({ status: 304, data: '', etag: 'W/"v1"' })
    await cache.get('k', 1000, fetcher)
    c.advance(1001)
    expect(await cache.get('k', 1000, fetcher)).toEqual({ n: 1 })
    expect(fetcher).toHaveBeenNthCalledWith(1, null)
    expect(fetcher).toHaveBeenNthCalledWith(2, 'W/"v1"')
    // expiry renewed after the 304
    c.advance(500)
    await cache.get('k', 1000, fetcher)
    expect(fetcher).toHaveBeenCalledTimes(2)
  })

  it('replaces data when revalidation returns 200', async () => {
    const c = clock()
    const cache = new HttpCache({ now: c.now })
    const fetcher = vi.fn().mockResolvedValueOnce(ok(1, 'W/"1"')).mockResolvedValueOnce(ok(2, 'W/"2"'))
    await cache.get('k', 10, fetcher)
    c.advance(11)
    expect(await cache.get('k', 10, fetcher)).toBe(2)
    expect(cache.peek('k')?.etag).toBe('W/"2"')
  })

  it('de-duplicates identical in-flight requests', async () => {
    const cache = new HttpCache()
    let resolve!: (v: FetchResult<number>) => void
    const fetcher = vi.fn(() => new Promise<FetchResult<number>>((r) => (resolve = r)))
    const p1 = cache.get('k', 1000, fetcher)
    const p2 = cache.get('k', 1000, fetcher)
    resolve(ok(42))
    expect(await Promise.all([p1, p2])).toEqual([42, 42])
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('does not share or keep failed requests', async () => {
    const cache = new HttpCache()
    const fetcher = vi.fn().mockRejectedValueOnce(new Error('boom')).mockResolvedValueOnce(ok(1))
    await expect(cache.get('k', 1000, fetcher)).rejects.toThrow('boom')
    expect(await cache.get('k', 1000, fetcher)).toBe(1)
  })

  it('force bypasses a fresh entry but still sends the ETag', async () => {
    const cache = new HttpCache()
    const fetcher = vi.fn().mockResolvedValueOnce(ok(1, 'W/"1"')).mockResolvedValueOnce(ok(2, 'W/"2"'))
    await cache.get('k', 60_000, fetcher)
    expect(await cache.get('k', 60_000, fetcher, { force: true })).toBe(2)
    expect(fetcher).toHaveBeenLastCalledWith('W/"1"')
  })

  it('ttl 0 never caches', async () => {
    const cache = new HttpCache()
    const fetcher = vi.fn().mockResolvedValue(ok(1))
    await cache.get('k', 0, fetcher)
    await cache.get('k', 0, fetcher)
    expect(fetcher).toHaveBeenCalledTimes(2)
    expect(cache.size).toBe(0)
  })

  it('evicts the least recently used entry beyond maxEntries', async () => {
    const cache = new HttpCache({ maxEntries: 2 })
    cache.set('a', 1, null, 1000)
    cache.set('b', 2, null, 1000)
    cache.peek('a') // touch a → b is now oldest
    cache.set('c', 3, null, 1000)
    expect(cache.peek('b')).toBeUndefined()
    expect(cache.peek('a')?.data).toBe(1)
    expect(cache.peek('c')?.data).toBe(3)
  })

  it('invalidates by path prefix only', () => {
    const cache = new HttpCache()
    const keys = [
      buildCacheKey('GET', '/stocks', { page: 1 }),
      buildCacheKey('GET', '/stocks/5'),
      buildCacheKey('GET', '/stocks/5/transactions'),
      buildCacheKey('GET', '/stock-trans', { page: 1 }),
      buildCacheKey('GET', '/dashboard/summary'),
      buildCacheKey('GET', '/companies'),
    ]
    keys.forEach((k, i) => cache.set(k, i, null, 60_000))
    cache.invalidate(['/stocks', '/dashboard'])
    expect(cache.getFresh(keys[0]!)).toBeUndefined()
    expect(cache.getFresh(keys[1]!)).toBeUndefined()
    expect(cache.getFresh(keys[2]!)).toBeUndefined()
    expect(cache.getFresh(keys[3]!)).toBe(3) // `/stock-trans` is not under `/stocks`
    expect(cache.getFresh(keys[4]!)).toBeUndefined()
    expect(cache.getFresh(keys[5]!)).toBe(5)
  })

  it('a response that started before an invalidation is not stored', async () => {
    const cache = new HttpCache()
    let resolve!: (v: FetchResult<number>) => void
    const fetcher = vi.fn(() => new Promise<FetchResult<number>>((r) => (resolve = r)))
    const p = cache.get('GET /stocks #c=', 60_000, fetcher)
    cache.invalidate(['/stocks'])
    resolve(ok(1))
    expect(await p).toBe(1)
    expect(cache.size).toBe(0)
  })

  it('clear removes everything', () => {
    const cache = new HttpCache()
    cache.set('a', 1, null, 1000)
    cache.clear()
    expect(cache.size).toBe(0)
  })
})
