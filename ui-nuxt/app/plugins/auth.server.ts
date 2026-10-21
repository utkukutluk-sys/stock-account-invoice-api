// SSR auth bootstrap (CONTRACT §10.7): refresh with the forwarded cookie and put the result into
// useState('auth'), which the payload hydrates on the client (no client refresh needed then).
import { ssrRefresh } from '~~/server/utils/ssrAuth'
import { AUTH_STATE_KEY, type AuthState } from '@/stores/auth'

export default defineNuxtPlugin({
  name: 'auth-ssr',
  enforce: 'pre',
  async setup() {
    const event = useRequestEvent()
    if (!event) return
    const { apiBase } = useRuntimeConfig()
    const result = await ssrRefresh(event, apiBase)
    if (!result) return
    useState<AuthState>(AUTH_STATE_KEY).value = {
      accessToken: result.accessToken,
      accessTokenExpiresAt: Date.parse(result.accessTokenExpiresAt) || null,
      user: result.user,
    }
  },
})
