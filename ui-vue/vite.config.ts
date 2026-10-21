/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url'
import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'
import Components from 'unplugin-vue-components/vite'
import { PrimeVueResolver } from '@primevue/auto-import-resolver'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const proxyTarget = env.VITE_PROXY_TARGET || 'http://localhost:5080'

  return {
    plugins: [
      vue(),
      // PrimeVue components are auto-imported on demand (tree-shaken), no global registration.
      Components({
        dts: 'components.d.ts',
        dirs: [],
        resolvers: [PrimeVueResolver()],
      }),
    ],
    // Pre-bundle every PrimeVue entry used by lazily loaded pages so the dev server never
    // re-optimizes (and force-reloads) on the first visit of a route — keeps E2E on `vite dev` stable.
    optimizeDeps: {
      include: [
        'vue', 'vue-router', 'pinia', 'axios', '@primeuix/themes', '@primeuix/themes/aura',
        'primevue/config', 'primevue/toastservice', 'primevue/confirmationservice', 'primevue/usetoast', 'primevue/useconfirm',
        'primevue/autocomplete', 'primevue/button', 'primevue/checkbox', 'primevue/column', 'primevue/confirmdialog',
        'primevue/datatable', 'primevue/datepicker', 'primevue/dialog', 'primevue/iconfield', 'primevue/inputicon',
        'primevue/inputnumber', 'primevue/inputtext', 'primevue/message', 'primevue/popover', 'primevue/select',
        'primevue/skeleton', 'primevue/tab', 'primevue/tablist', 'primevue/tabpanel', 'primevue/tabpanels', 'primevue/tabs',
        'primevue/tag', 'primevue/textarea', 'primevue/toast', 'primevue/toggleswitch', 'primevue/tooltip',
      ],
    },
    resolve: {
      alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
    },
    server: {
      port: 5173,
      strictPort: true,
      // Same-origin model (CONTRACT §8.1): the browser only ever calls relative /api.
      proxy: {
        '/api': { target: proxyTarget, changeOrigin: true },
      },
    },
    preview: {
      port: 5173,
      proxy: {
        '/api': { target: proxyTarget, changeOrigin: true },
      },
    },
    build: {
      target: 'es2022',
      chunkSizeWarningLimit: 700,
      rollupOptions: {
        output: {
          manualChunks(id: string) {
            if (!id.includes('node_modules')) return undefined
            if (id.includes('@primeuix') || id.includes('@primevue/core') || id.includes('@primevue/icons')) {
              return 'primevue-core'
            }
            if (/node_modules[\/](vue|@vue|vue-router|pinia)[\/]/.test(id)) return 'vue-vendor'
            if (id.includes('axios')) return 'axios'
            return undefined
          },
        },
      },
    },
    test: {
      environment: 'jsdom',
      include: ['src/**/*.spec.ts'],
    },
  }
})
