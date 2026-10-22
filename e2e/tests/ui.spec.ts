import { expect, test } from '@playwright/test';
import { gotoPage, hydrated, isNuxt, login } from './helpers';

// Dark-mode toggle and proactive token refresh (CONTRACT §10.2: refresh when the access token expires in < 60 s).

test('dark mode toggle applies .app-dark and persists across reload', async ({ page, request }, testInfo) => {
  await login(page, 'demo');
  const html = page.locator('html');
  await expect(html).not.toHaveClass(/app-dark/);
  await hydrated(page);
  await page.getByRole('button', { name: 'Koyu tema' }).click();
  await expect(html).toHaveClass(/app-dark/);
  const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);

  await page.reload();
  await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');
  await expect(html).toHaveClass(/app-dark/);
  expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe(bg);

  if (isNuxt(testInfo)) {
    // SSR renders the theme class already (cookie sa_theme) → no flash
    const cookies = await page.context().cookies();
    const theme = cookies.find((c) => c.name === 'sa_theme');
    expect(theme?.value).toBeTruthy();
    const res = await page.request.get('/login');
    expect(await res.text()).toMatch(/<html[^>]*class="[^"]*app-dark/);
  }
  void request;

  await hydrated(page);
  await page.getByRole('button', { name: 'Açık tema' }).click();
  await expect(html).not.toHaveClass(/app-dark/);
});

test('UI refreshes the access token proactively shortly before it expires', async ({ page }) => {
  await page.clock.install();
  await login(page, 'demo');
  await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');
  const refreshes: string[] = [];
  page.on('request', (r) => { if (r.url().includes('/api/auth/refresh') && r.method() === 'POST') refreshes.push(r.url()); });

  // jump the browser clock to 14:30 after login: the 15-min token now expires in < 60 s
  await page.clock.fastForward('14:30');
  await page.getByTestId('nav-stocks').click();
  await expect(page.getByTestId('page-title')).toHaveText('Stoklar');
  await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
  await expect.poll(() => refreshes.length).toBeGreaterThanOrEqual(1);

  // still logged in, and data requests keep working with the renewed token
  await gotoPage(page, '/current-accounts');
  await expect(page.getByTestId('account-table')).toContainText('C001');
});
