import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

vi.mock('@/api/auth', () => ({ authApi: {} }))

import { useCompanyGuard, COMPANY_REQUIRED_MESSAGE } from '../useCompanyGuard'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'

beforeEach(() => {
  setActivePinia(createPinia())
  document.cookie = 'sa_company=; Max-Age=0; path=/'
})

describe('useCompanyGuard / company-required notice state', () => {
  it('uses the new guidance message', () => {
    expect(COMPANY_REQUIRED_MESSAGE).toBe('İşlem yapmak için bir firma seçin. Firma seçimi sağ üstteki menüden de yapılabilir.')
  })

  it('needs a company for an admin with Tüm Firmalar, until one is selected via the shared store', () => {
    const auth = useAuthStore()
    auth.$patch({ user: { id: 1, role: 'Admin' } as never })
    const ui = useUiStore()
    ui.setCompany(null)
    const { needsCompany } = useCompanyGuard()
    expect(needsCompany.value).toBe(true)
    ui.setCompany(5) // same action the notice select and the top-bar switcher call
    expect(needsCompany.value).toBe(false)
  })
})
