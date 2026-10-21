import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import type { AuthResponse } from '@/types/api'

const authApi = vi.hoisted(() => ({
  login: vi.fn(),
  refresh: vi.fn(),
  logout: vi.fn(),
  me: vi.fn(),
  changePassword: vi.fn(),
}))
vi.mock('@/api/auth', () => ({ authApi }))

import { useAuthStore } from '../auth'
import { httpCache } from '@/api/http'

const response = (over: Partial<AuthResponse['user']> = {}, token = 'tok'): AuthResponse => ({
  accessToken: token,
  accessTokenExpiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
  user: {
    id: 1,
    email: 'demo@stockaccount.local',
    fullName: 'Demo',
    role: 'User',
    companyId: 1,
    companyName: 'Demo',
    currentAccountId: null,
    currentAccountTitle: null,
    isPortalUser: false,
    ...over,
  },
})

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
})

describe('auth store', () => {
  it('bootstrap restores the session from the refresh cookie once', async () => {
    authApi.refresh.mockResolvedValue(response())
    const auth = useAuthStore()
    await Promise.all([auth.bootstrap(), auth.bootstrap()])
    expect(auth.isAuthenticated).toBe(true)
    expect(auth.accessToken).toBe('tok')
    expect(authApi.refresh).toHaveBeenCalledTimes(1)
  })

  it('bootstrap treats 401 as anonymous after one delayed retry', async () => {
    vi.useFakeTimers()
    try {
      authApi.refresh.mockRejectedValue({ status: 401 })
      const auth = useAuthStore()
      const p = auth.bootstrap()
      await vi.advanceTimersByTimeAsync(500)
      await p
      expect(auth.isAuthenticated).toBe(false)
      expect(authApi.refresh).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('keeps tokens in memory only (no web storage)', async () => {
    authApi.login.mockResolvedValue(response())
    const auth = useAuthStore()
    await auth.login({ email: 'a@b.c', password: 'x' })
    expect(localStorage.length).toBe(0)
    expect(sessionStorage.length).toBe(0)
  })

  it('derives policies from the user', async () => {
    const auth = useAuthStore()
    authApi.login.mockResolvedValueOnce(response({ role: 'Admin', companyId: null }))
    await auth.login({ email: 'a', password: 'b' })
    expect([auth.can('admin'), auth.can('operator'), auth.can('portal'), auth.homePath]).toEqual([true, true, false, '/'])

    authApi.login.mockResolvedValueOnce(response({ currentAccountId: 5, isPortalUser: true }))
    await auth.login({ email: 'a', password: 'b' })
    expect([auth.can('admin'), auth.can('operator'), auth.can('portal'), auth.homePath]).toEqual([false, false, true, '/my-account'])

    authApi.login.mockResolvedValueOnce(response())
    await auth.login({ email: 'a', password: 'b' })
    expect([auth.can('admin'), auth.can('operator'), auth.can('portal')]).toEqual([false, true, false])
  })

  it('concurrent refresh calls share one request; failure clears the session', async () => {
    authApi.login.mockResolvedValue(response())
    const auth = useAuthStore()
    await auth.login({ email: 'a', password: 'b' })

    authApi.refresh.mockResolvedValueOnce(response({}, 'tok2'))
    expect(await Promise.all([auth.refresh(), auth.refresh()])).toEqual(['tok2', 'tok2'])
    expect(authApi.refresh).toHaveBeenCalledTimes(1)

    authApi.refresh.mockRejectedValue({ status: 500 })
    await expect(auth.refresh()).rejects.toBeTruthy()
    expect(auth.isAuthenticated).toBe(false)
  })

  it('logout clears state and the HTTP cache even if the API call fails', async () => {
    authApi.login.mockResolvedValue(response())
    authApi.logout.mockRejectedValue(new Error('network'))
    const auth = useAuthStore()
    await auth.login({ email: 'a', password: 'b' })
    httpCache.set('GET /stocks #c=', [], null, 60_000)
    await auth.logout()
    expect(auth.isAuthenticated).toBe(false)
    expect(httpCache.size).toBe(0)
  })
})
