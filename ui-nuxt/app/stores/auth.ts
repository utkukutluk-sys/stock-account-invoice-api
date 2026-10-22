import { defineStore } from 'pinia'
import { computed } from 'vue'
import type { AuthResponse, LoginRequest, UserInfo } from '@/types/api'
import { authApi } from '@/api/auth'
import { httpCache } from '@/utils/http'
import { createRefreshController } from '@/lib/refresh'

export type Policy = 'operator' | 'admin' | 'portal'

export interface AuthState {
  accessToken: string | null
  /** Epoch ms. */
  accessTokenExpiresAt: number | null
  user: UserInfo | null
}

/** Shared state key; SSR fills it (plugins/auth.server.ts) and the payload hydrates the client. */
export const AUTH_STATE_KEY = 'auth'

export function toAuthState(res: AuthResponse): AuthState {
  return {
    accessToken: res.accessToken,
    accessTokenExpiresAt: Date.parse(res.accessTokenExpiresAt) || null,
    user: res.user,
  }
}

// Created lazily in the browser only; on the server no refresh happens through the store.
let refresher: ReturnType<typeof createRefreshController<AuthResponse>> | null = null
let bootstrapPromise: Promise<void> | null = null

/** Auth state in memory only (CONTRACT §4.1/§10.2), backed by `useState('auth')`. */
export const useAuthStore = defineStore('auth', () => {
  const state = useState<AuthState>(AUTH_STATE_KEY, () => ({ accessToken: null, accessTokenExpiresAt: null, user: null }))

  const accessToken = computed(() => state.value.accessToken)
  const accessTokenExpiresAt = computed(() => state.value.accessTokenExpiresAt)
  const user = computed(() => state.value.user)
  const isAuthenticated = computed(() => !!state.value.accessToken && !!state.value.user)
  const isAdmin = computed(() => user.value?.role === 'Admin')
  const isPortal = computed(() => user.value?.role === 'User' && !!user.value?.isPortalUser)
  const isOperator = computed(() => isAdmin.value || (user.value?.role === 'User' && !user.value?.isPortalUser))
  const homePath = computed(() => (isPortal.value ? '/my-account' : '/'))

  function apply(res: AuthResponse) {
    state.value = toAuthState(res)
  }

  function clear() {
    state.value = { accessToken: null, accessTokenExpiresAt: null, user: null }
  }

  async function refresh(): Promise<string> {
    refresher ??= createRefreshController({ doRefresh: () => authApi.refresh() })
    try {
      const res = await refresher.refresh()
      apply(res)
      return res.accessToken
    } catch (err) {
      clear()
      throw err
    }
  }

  /** Browser bootstrap: silent refresh once, skipped when SSR already hydrated a token. */
  function bootstrap(): Promise<void> {
    if (state.value.accessToken || import.meta.server) return Promise.resolve()
    bootstrapPromise ??= refresh()
      .then(() => undefined)
      .catch(() => undefined)
    return bootstrapPromise
  }

  async function login(body: LoginRequest) {
    const res = await authApi.login(body)
    httpCache.clear()
    apply(res)
    return res.user
  }

  async function logout() {
    try {
      await authApi.logout()
    } catch {
      // idempotent
    }
    clear()
    httpCache.clear()
    // sa_company is cleared by the caller via the ui store (single useCookie instance).
  }

  function can(policy?: Policy): boolean {
    if (!isAuthenticated.value) return false
    if (!policy) return true
    if (policy === 'admin') return isAdmin.value
    if (policy === 'portal') return isPortal.value
    return isOperator.value
  }

  function setUser(u: UserInfo) {
    state.value = { ...state.value, user: u }
  }

  return {
    accessToken,
    accessTokenExpiresAt,
    user,
    isAuthenticated,
    isAdmin,
    isPortal,
    isOperator,
    homePath,
    apply,
    login,
    logout,
    refresh,
    bootstrap,
    clear,
    can,
    setUser,
  }
})
