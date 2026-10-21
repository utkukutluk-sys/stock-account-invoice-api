import Aura from '@primeuix/themes/aura'
import { primeVueTr } from './app/i18n/primevue-tr'

// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  compatibilityDate: '2026-10-01',
  ssr: true,
  devtools: { enabled: false },
  modules: ['@primevue/nuxt-module', '@pinia/nuxt'],

  css: ['primeicons/primeicons.css', 'primeflex/primeflex.css', '~/assets/main.css'],

  app: {
    head: {
      title: 'Stok Cari',
      htmlAttrs: { lang: 'tr' },
      meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1' }],
      link: [{ rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }],
    },
  },

  // Private (server-only): API origin without /api. Override with NUXT_API_BASE (CONTRACT §8.4).
  runtimeConfig: {
    apiBase: 'http://localhost:5080',
  },

  primevue: {
    // Components/directives are auto-imported on demand (tree-shaken); services are registered by the module.
    autoImport: true,
    options: {
      theme: {
        preset: Aura,
        options: { darkModeSelector: '.app-dark', cssLayer: false },
      },
      locale: primeVueTr,
      ripple: false,
    },
  },

  typescript: {
    strict: true,
    typeCheck: false,
  },

  devServer: { port: 3000 },

  vite: {
    optimizeDeps: {
      include: ['primevue/usetoast', 'primevue/useconfirm'],
    },
  },
})
