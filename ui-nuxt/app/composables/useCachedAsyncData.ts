import type { NuxtApp } from '#app'
import { computed, toValue, type MaybeRefOrGetter } from 'vue'
import { resolveTtl, sortedQuery } from '@/utils/cache'
import { httpCache } from '@/utils/http'
import { toApiError, type ApiError } from '@/utils/errors'
import { useApi } from './useApi'

/** useAsyncData wraps thrown errors in a NuxtError; unwrap to our ApiError. */
export function asApiError(e: unknown): ApiError {
  return toApiError((e as { cause?: unknown } | null)?.cause ?? e)
}

export interface CachedAsyncDataOptions {
  /** Load during SSR (default true). `false` = client-only (e.g. "Hareketler" tab). */
  server?: boolean
  immediate?: boolean
}

/**
 * `useAsyncData` keyed exactly like the client HTTP cache (CONTRACT §10.7):
 * key = request URL + sorted query + company id. `getCachedData` returns the SSR payload while
 * hydrating (and seeds the client cache with it), then a still-fresh client cache entry.
 */
export function useCachedAsyncData<T>(
  url: MaybeRefOrGetter<string>,
  params: MaybeRefOrGetter<Record<string, unknown> | null>,
  fetcher: (opts: { force: boolean }) => Promise<T>,
  opts: CachedAsyncDataOptions = {},
) {
  const api = useApi()
  const key = computed(() => api.keyFor(toValue(url), toValue(params)))
  let forceNext = false

  function getCachedData(k: string, nuxtApp: NuxtApp, ctx?: { cause?: string }): T | undefined {
    if (ctx?.cause === 'refresh:manual') return undefined
    const payloadData = nuxtApp.payload.data[k] as T | undefined
    if (nuxtApp.isHydrating && payloadData !== undefined) {
      if (import.meta.client) {
        const qs = sortedQuery(toValue(params) ?? undefined)
        const u = toValue(url)
        httpCache.set(k, payloadData, null, resolveTtl(qs ? `${u}?${qs}` : u))
      }
      return payloadData
    }
    return import.meta.client ? httpCache.getFresh<T>(k) : undefined
  }

  const result = useAsyncData<T>(
    key,
    () => {
      const force = forceNext
      forceNext = false
      return fetcher({ force })
    },
    { server: opts.server ?? true, immediate: opts.immediate ?? true, deep: false, getCachedData },
  )

  /** Reload bypassing the client cache ("Yenile"). */
  async function forceRefresh() {
    forceNext = true
    await result.refresh()
  }

  // Usable both synchronously and with `await` (the awaited value carries the extras too).
  const extras = { key, forceRefresh }
  const awaited = Promise.resolve(result).then((r) => Object.assign(r, extras))
  return Object.assign(awaited, result, extras)
}
