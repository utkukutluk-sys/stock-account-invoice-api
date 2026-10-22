import { expect, test } from '@playwright/test';
import { gotoPage, isNuxt, login, switchCompany } from './helpers';

// Independent hydration check (Nuxt only): full SSR loads of the main pages in a browser time zone far from the server's,
// in light and dark theme; any Vue hydration warning/error in the console fails the test.
test.use({ timezoneId: 'America/Los_Angeles' });

test('Nuxt: no hydration mismatches on SSR pages (light + dark, foreign time zone)', async ({ page }, testInfo) => {
  test.skip(!isNuxt(testInfo), 'Nuxt only');
  const problems: string[] = [];
  page.on('console', (m) => { if (/hydrat/i.test(m.text())) problems.push(`${page.url()} → ${m.type()}: ${m.text().slice(0, 200)}`); });
  page.on('pageerror', (e) => problems.push(`${page.url()} → pageerror: ${e.message.slice(0, 200)}`));

  await login(page, 'admin');
  await switchCompany(page);
  const pages = ['/', '/stocks', '/current-accounts', '/invoices', '/invoices/new', '/stock-transactions', '/account-transactions', '/companies', '/users', '/reconciliation', '/profile', '/stocks/1', '/current-accounts/1'];
  for (const theme of ['light', 'dark']) {
    if (theme === 'dark') await page.getByRole('button', { name: 'Koyu tema' }).click();
    for (const p of pages) {
      await gotoPage(page, p);
      await expect(page.getByTestId('page-title')).toBeVisible();
    }
  }
  await page.getByRole('button', { name: 'Açık tema' }).click();
  expect(problems, problems.join('\n')).toEqual([]);
});
