import type { ApiClient } from '@/utils/http'

/** The per-request API client provided by plugins/api.ts. */
export function useApi(): ApiClient {
  return useNuxtApp().$api as ApiClient
}
