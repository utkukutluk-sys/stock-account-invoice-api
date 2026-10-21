import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { httpCache } from '@/utils/http'

const YEAR = 60 * 60 * 24 * 365

export const useUiStore = defineStore('ui', () => {
  // `sa_company` is readable on the server so SSR sends the same X-Company-Id (CONTRACT §10.2).
  const companyCookie = useCookie<string | null>('sa_company', { path: '/', sameSite: 'strict', maxAge: YEAR, default: () => null })
  // Theme in a cookie (UI preference only) so SSR renders the right color scheme without a flash.
  const themeCookie = useCookie<string | null>('sa_theme', { path: '/', sameSite: 'strict', maxAge: YEAR, default: () => null })

  const companyId = computed<number | null>(() => {
    const v = Number(companyCookie.value)
    return Number.isInteger(v) && v > 0 ? v : null
  })
  const companyVersion = ref(0)
  const companyListVersion = ref(0)
  const sidebarOpen = ref(false)
  const dark = computed(() => themeCookie.value === 'dark')

  function toggleDark() {
    themeCookie.value = dark.value ? 'light' : 'dark'
  }

  function setCompany(id: number | null) {
    if (id === companyId.value) return
    companyCookie.value = id ? String(id) : null
    httpCache.clear()
    companyVersion.value++
  }

  /** No-op kept for API parity with ui-vue (the cookie ref is reactive here). */
  function syncCompany() {}

  return { companyId, companyVersion, companyListVersion, sidebarOpen, dark, toggleDark, setCompany, syncCompany }
})
