// Fails when any SSR page logs a Vue hydration warning in the browser console.
// Usage (against a running production build): BASE=http://localhost:3000 npm run check:hydration
// The browser deliberately runs in a different time zone than the server to catch TZ-dependent output.
import { chromium } from 'playwright'

const BASE = (process.env.BASE || 'http://localhost:3000').replace(/\/$/, '')
const EMAIL = process.env.CHECK_EMAIL || 'admin@stockaccount.local'
const PASSWORD = process.env.CHECK_PASSWORD || 'Admin123!'
const COMPANY = process.env.CHECK_COMPANY || 'Demo Gıda A.Ş.'
const TIMEZONES = (process.env.CHECK_TIMEZONES || 'America/New_York,Asia/Tokyo').split(',')

const findings = []
const browser = await chromium.launch()
try {
  for (const timezoneId of TIMEZONES) {
    const context = await browser.newContext({ viewport: { width: 1400, height: 900 }, timezoneId, locale: 'tr-TR' })
    const page = await context.newPage()
    let current = ''
    page.on('console', (m) => {
      if (/hydrat/i.test(m.text())) findings.push(`[${timezoneId}] ${current}: ${m.text().split('\n')[0].slice(0, 200)}`)
    })
    page.on('pageerror', (e) => findings.push(`[${timezoneId}] ${current}: pageerror ${e.message}`))
    const tid = (id) => page.getByTestId(id)

    current = '/login (anonymous)'
    await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' })
    await tid('login-email').fill(EMAIL)
    await tid('login-password').fill(PASSWORD)
    await tid('login-submit').click()
    await page.waitForURL(`${BASE}/`)
    await tid('company-switcher').click()
    await page.getByRole('option', { name: COMPANY }).click()
    await page.waitForTimeout(500)

    // Full SSR loads (page.goto) of the main pages, in light and dark theme.
    const pages = ['/', '/stocks', '/current-accounts', '/invoices', '/invoices/new', '/stock-transactions', '/account-transactions', '/users', '/profile']
    current = '/invoices (find record ids)'
    await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' })
    const invoiceHref = await page.evaluate(() => document.querySelector('[data-testid="invoice-table"] tbody tr') ? 'x' : null)
    for (const theme of ['light', 'dark']) {
      await context.addCookies([{ name: 'sa_theme', value: theme, url: BASE }])
      for (const path of pages) {
        current = `${path} (${theme})`
        await page.goto(`${BASE}${path}`, { waitUntil: 'networkidle' })
        await page.waitForTimeout(300)
      }
      if (invoiceHref) {
        current = `/invoices/:id (${theme})`
        await page.goto(`${BASE}/invoices`, { waitUntil: 'networkidle' })
        await page.getByTestId('invoice-table').locator('tbody tr').first().locator('[data-testid="row-view"],[data-testid="row-edit"]').first().click()
        await page.waitForURL(/\/invoices\/\d+$/)
        await page.reload({ waitUntil: 'networkidle' })
        await page.waitForTimeout(300)
      }
    }
    await context.clearCookies()
    current = '/login (after logout)'
    await page.goto(`${BASE}/login`, { waitUntil: 'networkidle' })
    await context.close()
  }
} finally {
  await browser.close()
}

if (findings.length) {
  console.error(`Hydration problems (${findings.length}):\n${findings.join('\n')}`)
  process.exit(1)
}
console.log(`OK: no hydration warnings on ${TIMEZONES.length} browser time zone(s) at ${BASE}`)
