import { createApp } from 'vue'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import Tooltip from 'primevue/tooltip'
import Aura from '@primeuix/themes/aura'
import { definePreset } from '@primeuix/themes'

import 'primeicons/primeicons.css'
import 'primeflex/primeflex.css'
import './assets/main.css'

import App from './App.vue'
import { router } from './router'
import { configureHttp, httpCache } from './api/http'
import { useAuthStore } from './stores/auth'
import { useUiStore } from './stores/ui'
import { readCompanyCookie } from './utils/cookies'
import { primeVueTr } from './i18n/primevue-tr'

const Preset = definePreset(Aura, {
  semantic: {
    primary: {
      50: '{emerald.50}', 100: '{emerald.100}', 200: '{emerald.200}', 300: '{emerald.300}', 400: '{emerald.400}',
      500: '{emerald.500}', 600: '{emerald.600}', 700: '{emerald.700}', 800: '{emerald.800}', 900: '{emerald.900}', 950: '{emerald.950}',
    },
  },
})

const app = createApp(App)
const pinia = createPinia()
app.use(pinia)
app.use(PrimeVue, {
  theme: {
    preset: Preset,
    options: { darkModeSelector: '.app-dark', cssLayer: false },
  },
  locale: primeVueTr,
  ripple: false,
})
app.use(ToastService)
app.use(ConfirmationService)
app.directive('tooltip', Tooltip)

const auth = useAuthStore(pinia)
const ui = useUiStore(pinia)
ui.applyTheme()

configureHttp({
  getAccessToken: () => auth.accessToken,
  getExpiresAt: () => auth.accessTokenExpiresAt,
  isAdmin: () => auth.isAdmin,
  getCompanyId: () => readCompanyCookie(),
  refresh: () => auth.refresh(),
  onAuthFailure: () => {
    auth.clear()
    httpCache.clear()
    const current = router.currentRoute.value
    if (current.name !== 'login') {
      void router.replace({ name: 'login', query: current.meta.public ? {} : { redirect: current.fullPath } })
    }
  },
})

// Global error boundary: friendly Turkish message; technical details only in the console during development.
app.config.errorHandler = (err, _instance, info) => {
  if (import.meta.env.DEV) console.error('[app error]', info, err)
  app.config.globalProperties.$toast?.add({
    severity: 'error',
    summary: 'Hata',
    detail: 'Beklenmeyen bir hata oluştu. Lütfen sayfayı yenileyip tekrar deneyin.',
    life: 6000,
  })
}
// A lazily loaded page chunk that vanished after a redeploy: reload the target URL once.
router.onError((err, to) => {
  if (/Failed to fetch dynamically imported module|Importing a module script failed/i.test(String(err?.message))) {
    window.location.assign(to.fullPath)
  } else if (import.meta.env.DEV) {
    console.error('[router error]', err)
  }
})

app.use(router)
void router.isReady().then(() => app.mount('#app'))
