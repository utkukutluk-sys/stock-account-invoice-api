import { defineStore } from 'pinia'
import { ref } from 'vue'
import { httpCache } from '@/api/http'
import { readCompanyCookie, writeCompanyCookie } from '@/utils/cookies'

const THEME_KEY = 'sa_theme'

function readDark(): boolean {
  try {
    const v = localStorage.getItem(THEME_KEY)
    if (v) return v === 'dark'
  } catch {
    /* storage unavailable */
  }
  return typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-color-scheme: dark)').matches
}

export const useUiStore = defineStore('ui', () => {
  /** Admin's selected company (`sa_company` cookie). null = "Tüm Firmalar". */
  const companyId = ref<number | null>(readCompanyCookie())
  /** Bumped on company switch so views remount and reload with the new X-Company-Id. */
  const companyVersion = ref(0)
  /** Bumped after company CRUD so the switcher reloads its options. */
  const companyListVersion = ref(0)
  /** Company list shared by the top-bar switcher and the in-page notice (loaded by the switcher). */
  const companies = ref<{ id: number; name: string }[]>([])
  const sidebarOpen = ref(false)
  const dark = ref(readDark())

  function applyTheme() {
    if (typeof document !== 'undefined') document.documentElement.classList.toggle('app-dark', dark.value)
  }

  function toggleDark() {
    dark.value = !dark.value
    try {
      localStorage.setItem(THEME_KEY, dark.value ? 'dark' : 'light') // UI preference only, no auth data
    } catch {
      /* ignore */
    }
    applyTheme()
  }

  function setCompany(id: number | null) {
    if (id === companyId.value) return
    companyId.value = id
    writeCompanyCookie(id)
    httpCache.clear()
    companyVersion.value++
  }

  /** Re-read the cookie (e.g. after logout cleared it). */
  function syncCompany() {
    companyId.value = readCompanyCookie()
  }

  return { companies, companyId, companyVersion, companyListVersion, sidebarOpen, dark, toggleDark, applyTheme, setCompany, syncCompany }
})
