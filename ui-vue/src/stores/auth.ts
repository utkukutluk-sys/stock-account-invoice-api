import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type { AuthResponse, LoginRequest, UserInfo } from '@/types/api'
import { authApi } from '@/api/auth'
import { httpCache } from '@/api/http'
import { createRefreshController } from '@/api/refresh'
import { writeCompanyCookie } from '@/utils/cookies'

export type Policy = 'operator' | 'admin' | 'portal'

/**
 * Auth state lives in memory only (CONTRACT §4.1/§10.2). The refresh token is the httpOnly
 * `sa_refresh` cookie, so a page reload recovers the session through `bootstrap()`.
 */
export const useAuthStore = defineStore('auth', () => {
  const accessToken = ref<string | null>(null)
  const accessTokenExpiresAt = ref<number | null>(null)
  const user = ref<UserInfo | null>(null)
  let bootstrapPromise: Promise<void> | null = null

  const isAuthenticated = computed(() => !!accessToken.value && !!user.value)
  const isAdmin = computed(() => user.value?.role === 'Admin')
  const isPortal = computed(() => user.value?.role === 'User' && !!user.value?.isPortalUser)
  const isOperator = computed(() => isAdmin.value || (user.value?.role === 'User' && !user.value?.isPortalUser))
  /** Landing page after login (CONTRACT §10.2). */
  const homePath = computed(() => (isPortal.value ? '/my-account' : '/'))

  function apply(res: AuthResponse) {
    accessToken.value = res.accessToken
    accessTokenExpiresAt.value = Date.parse(res.accessTokenExpiresAt) || null
    user.value = res.user
  }

  function clear() {
    accessToken.value = null
    accessTokenExpiresAt.value = null
    user.value = null
  }

  const refresher = createRefreshController({ doRefresh: () => authApi.refresh() })

  /** Single-flight refresh. Resolves to the new access token; on failure clears the session and rethrows. */
  async function refresh(): Promise<string> {
    try {
      const res = await refresher.refresh()
      apply(res)
      return res.accessToken
    } catch (err) {
      clear()
      throw err
    }
  }

  /** Silent refresh once at startup (no-op when a token is already in memory). */
  function bootstrap(): Promise<void> {
    if (accessToken.value) return Promise.resolve()
    if (!bootstrapPromise) {
      bootstrapPromise = refresh()
        .then(() => undefined)
        .catch(() => undefined)
    }
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
      // logout is idempotent; local state is cleared regardless
    }
    clear()
    httpCache.clear()
    writeCompanyCookie(null)
  }

  function can(policy?: Policy): boolean {
    if (!isAuthenticated.value) return false
    if (!policy) return true
    if (policy === 'admin') return isAdmin.value
    if (policy === 'portal') return isPortal.value
    return isOperator.value
  }

  function setUser(u: UserInfo) {
    user.value = u
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
    login,
    logout,
    refresh,
    bootstrap,
    clear,
    can,
    setUser,
  }
})
