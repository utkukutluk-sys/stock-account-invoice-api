// Shared API client (CONTRACT §10.7): server → apiBase + '/api' with the SSR token,
// browser → same-origin '/api' with the client HTTP cache and refresh handling.
import { createApiClient, httpCache, type ApiClient } from '@/utils/http'
import { useAuthStore } from '@/stores/auth'
import { forwardedFor } from '~~/server/utils/ssrAuth'

export default defineNuxtPlugin({
  name: 'api',
  setup(nuxtApp): { provide: { api: ApiClient } } {
    const auth = useAuthStore(nuxtApp.$pinia as never)
    const companyCookie = useCookie<string | null>('sa_company', { path: '/', sameSite: 'strict' })
    const getCompanyId = () => {
      const v = Number(companyCookie.value)
      return Number.isInteger(v) && v > 0 ? v : null
    }
    const common = {
      getAccessToken: () => auth.accessToken,
      getExpiresAt: () => auth.accessTokenExpiresAt,
      isAdmin: () => auth.isAdmin,
      getCompanyId,
    }

    if (import.meta.server) {
      const event = useRequestEvent()
      const { apiBase } = useRuntimeConfig()
      // SSR calls the API directly: forward the browser's address chain for rate limiting/logging.
      const xff = event ? forwardedFor(event) : undefined
      const proto = event?.node.req.headers['x-forwarded-proto']
      return {
        provide: {
          api: createApiClient({ ...common, baseURL: `${apiBase}/api`, cache: null, headers: {
            ...(xff ? { 'x-forwarded-for': xff } : {}),
            ...(typeof proto === 'string' ? { 'x-forwarded-proto': proto } : {}),
          },
        }),
        },
      }
    }

    return {
      provide: {
        api: createApiClient({
          ...common,
          baseURL: '/api',
          cache: httpCache,
          refresh: () => auth.refresh(),
          onAuthFailure: () => {
            auth.clear()
            httpCache.clear()
            const route = useRouter().currentRoute.value
            if (route.path !== '/login') {
              void navigateTo({ path: '/login', query: route.meta.public ? {} : { redirect: route.fullPath } }, { replace: true })
            }
          },
        }),
      },
    }
  },
})
