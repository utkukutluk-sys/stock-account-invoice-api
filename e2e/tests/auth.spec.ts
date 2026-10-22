import { expect, test } from '@playwright/test';
import { gotoPage, isNuxt, USERS, login, logout } from './helpers';

// CONTRACT §11.4 #1, #6, #7: login/logout, role-based menu, guards.
test.describe('auth & role-based menu', () => {
  test('invalid login shows error; admin login lands on dashboard @smoke', async ({ page }, testInfo) => {
    await gotoPage(page, '/login');
    await expect(page.getByTestId('page-title')).toHaveText('Giriş');
    await page.getByTestId('login-email').fill(USERS.admin.email);
    await page.getByTestId('login-password').fill('Wrong-Password1');
    await page.getByTestId('login-submit').click();
    await expect(page.getByTestId('login-error')).toBeVisible();
    await expect(page).toHaveURL(/\/login/);

    await page.getByTestId('login-password').fill(USERS.admin.password);
    await page.getByTestId('login-submit').click();
    await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');
    for (const id of ['nav-dashboard', 'nav-stocks', 'nav-current-accounts', 'nav-invoices', 'nav-stock-transactions', 'nav-account-transactions', 'nav-companies', 'nav-users', 'nav-reconciliation']) {
      await expect(page.getByTestId(id)).toBeVisible();
    }
    await expect(page.getByTestId('nav-my-account')).toHaveCount(0);
    await expect(page.getByTestId('company-switcher')).toBeVisible();

    // Nuxt: logging out on the same document as a client-side login freezes the page (BUG-4, separate test below),
    // so do a full SSR load first to still verify logout itself.
    if (isNuxt(testInfo)) await gotoPage(page, '/');
    await logout(page);
    // after logout guarded routes redirect to login with redirect param
    await gotoPage(page, '/stocks');
    await expect(page).toHaveURL(/\/login\?redirect=/);
  });

  test('login redirect query is honoured', async ({ page }) => {
    await gotoPage(page, '/invoices');
    await expect(page).toHaveURL(/\/login\?redirect=%2Finvoices|\/login\?redirect=\/invoices/);
    await page.getByTestId('login-email').fill(USERS.demo.email);
    await page.getByTestId('login-password').fill(USERS.demo.password);
    await page.getByTestId('login-submit').click();
    await expect(page).toHaveURL(/\/invoices$/);
    await expect(page.getByTestId('page-title')).toHaveText('Faturalar');
  });

  test('operator: no admin menu, /companies → /403', async ({ page }) => {
    await login(page, 'demo');
    await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');
    await expect(page.getByTestId('nav-stocks')).toBeVisible();
    await expect(page.getByTestId('nav-companies')).toHaveCount(0);
    await expect(page.getByTestId('nav-users')).toHaveCount(0);
    await expect(page.getByTestId('nav-reconciliation')).toHaveCount(0);
    await expect(page.getByTestId('company-switcher')).toHaveCount(0);
    await gotoPage(page, '/companies');
    await expect(page).toHaveURL(/\/403$/);
    await expect(page.getByTestId('page-title')).toHaveText('Yetkisiz Erişim');
  });

  test('portal user lands on /my-account, no operator menu, /stocks → /403 @smoke', async ({ page }) => {
    await login(page, 'ahmet');
    await expect(page).toHaveURL(/\/my-account$/);
    await expect(page.getByTestId('my-balance')).toBeVisible();
    await expect(page.getByTestId('nav-my-account')).toBeVisible();
    await expect(page.getByTestId('nav-stocks')).toHaveCount(0);
    await expect(page.getByTestId('nav-dashboard')).toHaveCount(0);
    await gotoPage(page, '/stocks');
    await expect(page).toHaveURL(/\/403$/);
  });

  test('logged-in user visiting /login is redirected; unknown route shows 404 page', async ({ page }) => {
    await login(page, 'demo');
    await gotoPage(page, '/login');
    await expect(page).not.toHaveURL(/\/login/);
    await gotoPage(page, '/this-page-does-not-exist');
    await expect(page.getByTestId('page-title')).toHaveText('Sayfa Bulunamadı');
  });

  test('page stays responsive after logout following a client-side login (BUG-4 regression)', async ({ page }) => {
    await login(page, 'demo');
    await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');
    await page.getByTestId('logout-button').click();
    await expect(page).toHaveURL(/\/login/, { timeout: 30_000 });
    const state = await Promise.race([
      page.evaluate(() => 'responsive'),
      new Promise<string>((r) => setTimeout(() => r('hung'), 8_000)),
    ]);
    expect(state).toBe('responsive');
    await page.getByTestId('login-email').fill(USERS.demo.email, { timeout: 5_000 });
  });
});
