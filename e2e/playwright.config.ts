import { defineConfig, devices } from '@playwright/test';

/**
 * E2E for ui-vue (5173) and ui-nuxt (3000) against a dedicated API (5081) on a fresh StockAccountDb_E2E database.
 *
 *   npm run test:vue        → starts API + vite dev (proxy → 5081), runs project "vue"
 *   npm run test:nuxt       → starts API + nuxt dev (NUXT_API_BASE → 5081), runs project "nuxt"
 *   npm test                → both
 *   E2E_EXTERNAL=1          → start nothing; use E2E_VUE_URL / E2E_NUXT_URL (e.g. docker compose stack)
 *   E2E_BASE_URL            → overrides the base URL of every project
 */
const ui = process.env.E2E_UI ?? 'both';
const external = process.env.E2E_EXTERNAL === '1';
const apiPort = process.env.E2E_API_PORT ?? '5081';
const vueUrl = process.env.E2E_BASE_URL ?? process.env.E2E_VUE_URL ?? 'http://localhost:5173';
const nuxtUrl = process.env.E2E_BASE_URL ?? process.env.E2E_NUXT_URL ?? 'http://localhost:3000';

const webServer = external
  ? []
  : [
      {
        command: 'node scripts/start-api.mjs',
        url: `http://localhost:${apiPort}/health`,
        timeout: 180_000,
        reuseExistingServer: false,
        stdout: 'ignore' as const,
        stderr: 'pipe' as const,
      },
      ...(ui === 'vue' || ui === 'both'
        ? [{
            command: 'npm run dev -- --port 5173 --strictPort',
            cwd: '../ui-vue',
            url: 'http://localhost:5173',
            env: { VITE_PROXY_TARGET: `http://localhost:${apiPort}` },
            timeout: 120_000,
            reuseExistingServer: false,
          }]
        : []),
      ...(ui === 'nuxt' || ui === 'both'
        ? [{
            command: 'npm run dev',
            cwd: '../ui-nuxt',
            url: 'http://localhost:3000/login',
            env: { NUXT_API_BASE: `http://localhost:${apiPort}` },
            timeout: 180_000,
            reuseExistingServer: false,
          }]
        : []),
    ];

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1, // tests share one seeded DB; each test uses unique E2E{timestamp} data, but balances of ELMA/C001 are shared
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { open: 'never' }], ['json', { outputFile: 'test-results/results.json' }]],
  use: {
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
  },
  projects: [
    { name: 'vue', testIgnore: /mobile.spec.ts/, use: { ...devices['Desktop Chrome'], baseURL: vueUrl } },
    { name: 'nuxt', testIgnore: /mobile.spec.ts/, use: { ...devices['Desktop Chrome'], baseURL: nuxtUrl } },
    // phone viewport smoke (Chromium device emulation)
    { name: 'vue-mobile', testMatch: /mobile.spec.ts/, use: { ...devices['Pixel 7'], baseURL: vueUrl } },
    { name: 'nuxt-mobile', testMatch: /mobile.spec.ts/, use: { ...devices['Pixel 7'], baseURL: nuxtUrl } },
  ],
  webServer,
});
