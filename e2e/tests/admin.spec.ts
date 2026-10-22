import { spawnSync } from 'node:child_process';
import { expect, test } from '@playwright/test';
import { Api, DEMO_COMPANY, confirmYes, dataValue, expectToast, gotoPage, hydrated, login, logout, pickAutoComplete, selectOption, uniq } from './helpers';

// Admin screens (CONTRACT §10.4): companies CRUD, users CRUD (portal user linked to an account), reconciliation.
const external = process.env.E2E_EXTERNAL === '1';

/** Direct SQL on the E2E database (local runs only) to tamper a stored balance for the reconciliation screen. */
function sql(query: string) {
  const db = process.env.E2E_DB ?? 'StockAccountDb_E2E';
  const r = spawnSync('sqlcmd', ['-S', process.env.E2E_SQL_SERVER ?? '.\\SQLEXPRESS', '-E', '-b', '-I', '-d', db, '-Q', query], { encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`sqlcmd failed: ${r.stdout}${r.stderr}`);
}

test.describe('admin screens', () => {
  test('companies: create → search → edit → delete @smoke', async ({ page }) => {
    const name = `E2E Firma ${uniq('')}`;
    const tax = String(1_000_000_000 + Math.floor(Math.random() * 8_999_999_999));
    await login(page, 'admin');
    await page.getByTestId('nav-companies').click();
    await expect(page.getByTestId('page-title')).toHaveText('Firmalar');

    await page.getByTestId('company-new').click();
    await page.getByTestId('company-name').fill(name);
    await page.getByTestId('company-tax-number').fill(tax);
    await page.getByTestId('company-tax-office').fill('Kadıköy');
    await page.getByTestId('company-city').fill('Eskişehir');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');

    await page.getByTestId('company-search').fill(name);
    const row = page.getByTestId('company-table').locator('tbody tr').filter({ hasText: name });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText(tax);

    await row.getByTestId('row-edit').click();
    await expect(page.getByTestId('company-name')).toHaveValue(name);
    await page.getByTestId('company-name').fill(`${name} 2`);
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');
    const edited = page.getByTestId('company-table').locator('tbody tr').filter({ hasText: `${name} 2` });
    await page.getByTestId('company-search').fill(`${name} 2`);
    await expect(edited).toHaveCount(1);

    // the new company appears in the company switcher
    await page.getByTestId('company-switcher').click();
    await expect(page.getByRole('option', { name: `${name} 2` })).toBeVisible();
    await page.keyboard.press('Escape');

    await edited.getByTestId('row-delete').click();
    await confirmYes(page);
    await expectToast(page, 'Başarılı');
    await expect(page.getByTestId('company-table').locator('tbody tr').filter({ hasText: name })).toHaveCount(0);
  });

  test('companies: duplicate tax number and non-empty company delete show error toasts', async ({ page }) => {
    await login(page, 'admin');
    await page.getByTestId('nav-companies').click();
    await page.getByTestId('company-new').click();
    await page.getByTestId('company-name').fill('Kopya Demo');
    await page.getByTestId('company-tax-number').fill('1234567890'); // seed Demo tax number
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Hata');
    await page.keyboard.press('Escape');

    await page.getByTestId('company-search').fill(DEMO_COMPANY);
    const demo = page.getByTestId('company-table').locator('tbody tr').filter({ hasText: DEMO_COMPANY });
    await demo.getByTestId('row-delete').click();
    await confirmYes(page);
    await expectToast(page, 'Hata');
    await expect(demo).toHaveCount(1);
  });

  test('users: create portal user linked to an account → he can log in → edit → delete', async ({ page, baseURL }) => {
    const api = await Api.as(baseURL!, 'demo');
    const account = await api.createAccount('Customer', uniq('E2EU'), 'E2E Portal Müşterisi');
    await api.dispose();
    const email = `e2e.${uniq('').toLowerCase()}@test.local`;

    await login(page, 'admin');
    await page.getByTestId('nav-users').click();
    await expect(page.getByTestId('page-title')).toHaveText('Kullanıcılar');
    await page.getByTestId('user-new').click();
    await page.getByTestId('user-email').fill(email);
    await page.getByTestId('user-full-name').fill('E2E Portal Kullanıcı');
    await page.getByTestId('user-password').fill('Passw0rdE2E');
    await selectOption(page, 'user-role', 'Kullanıcı');
    await selectOption(page, 'user-company', DEMO_COMPANY);
    await pickAutoComplete(page, 'user-current-account', account.code, new RegExp(account.code));
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');

    await page.getByTestId('user-search').fill(email);
    const row = page.getByTestId('user-table').locator('tbody tr').filter({ hasText: email });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText(DEMO_COMPANY);
    await expect(row).toContainText('E2E Portal Müşterisi');

    // the new user is a portal user
    await logout(page);
    await page.getByTestId('login-email').fill(email);
    await page.getByTestId('login-password').fill('Passw0rdE2E');
    await page.getByTestId('login-submit').click();
    await expect(page).toHaveURL(/\/my-account$/);
    await expect(page.getByTestId('my-balance')).toHaveAttribute('data-value', '0');
    await logout(page);

    await login(page, 'admin');
    await page.getByTestId('nav-users').click();
    await page.getByTestId('user-search').fill(email);
    await row.getByTestId('row-edit').click();
    await expect(page.getByTestId('user-email')).toHaveValue(email);
    await page.getByTestId('user-full-name').fill('E2E Portal Kullanıcı 2');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');
    await expect(page.getByTestId('user-table').locator('tbody tr').filter({ hasText: 'E2E Portal Kullanıcı 2' })).toHaveCount(1);

    await page.getByTestId('user-table').locator('tbody tr').filter({ hasText: email }).getByTestId('row-delete').click();
    await confirmYes(page);
    await expectToast(page, 'Başarılı');
    await expect(page.getByTestId('user-table').locator('tbody tr').filter({ hasText: email })).toHaveCount(0);
  });

  test('users: validation and duplicate e-mail', async ({ page }) => {
    await login(page, 'admin');
    await page.getByTestId('nav-users').click();
    await page.getByTestId('user-new').click();
    await page.getByTestId('user-email').fill('demo@stockaccount.local');
    await page.getByTestId('user-full-name').fill('Kopya');
    await page.getByTestId('user-password').fill('Passw0rdE2E');
    await selectOption(page, 'user-role', 'Kullanıcı');
    await selectOption(page, 'user-company', DEMO_COMPANY);
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Hata');
  });

  test('reconciliation screen: run, detect tampered balance, fix', async ({ page, baseURL }) => {
    await login(page, 'admin');
    await page.getByTestId('nav-reconciliation').click();
    await expect(page.getByTestId('page-title')).toHaveText('Bakiye Mutabakatı');
    await page.getByTestId('recon-run').click();
    await expect(page.getByTestId('recon-result')).toBeVisible();
    expect(await dataValue(page, 'recon-discrepancy-count')).toBe(0);
    test.skip(external, 'tampering needs direct SQL access to the E2E database');

    const api = await Api.as(baseURL!, 'demo');
    const stock = await api.createStock();
    await api.dispose();
    sql(`UPDATE Stocks SET Balance = 7 WHERE Id = ${stock.id}`);

    await page.getByTestId('recon-run').click();
    await expect(page.getByTestId('recon-discrepancy-count')).toHaveAttribute('data-value', '1');
    await expect(page.getByTestId('recon-result')).toContainText(stock.code);

    await hydrated(page);
    await page.getByTestId('recon-fix').click();
    await page.getByTestId('recon-run').click();
    await expectToast(page, 'Başarılı');
    await page.getByTestId('recon-fix').click(); // back to check-only
    await page.getByTestId('recon-run').click();
    await expect(page.getByTestId('recon-discrepancy-count')).toHaveAttribute('data-value', '0');
  });
});
