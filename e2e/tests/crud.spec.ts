import { expect, test } from '@playwright/test';
import { gotoPage, Api, confirmYes, expectToast, fillNumber, login, selectOption, switchCompany, uniq } from './helpers';

// CONTRACT §11.4 #2, #3, #8: stock & current account CRUD (admin in Demo), paging and filter.
test.describe('stock & current account CRUD', () => {
  test('stock: create → search → edit → delete @smoke', async ({ page }) => {
    const code = uniq();
    await login(page, 'admin');
    await switchCompany(page);
    await page.getByTestId('nav-stocks').click();
    await expect(page.getByTestId('page-title')).toHaveText('Stoklar');
    await page.getByTestId('stock-new').click();

    await page.getByTestId('stock-code').fill(code.toLowerCase()); // normalized to upper case by the API
    await page.getByTestId('stock-name').fill('E2E Elma Türü');
    await selectOption(page, 'stock-unit', 'Kg');
    await selectOption(page, 'stock-vat-rate', '%10');
    await fillNumber(page, 'stock-unit-price', '12.5');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');
    await expect(page).toHaveURL(/\/stocks\/\d+$/);
    await expect(page.getByTestId('stock-code')).toHaveValue(code);
    await expect(page.getByTestId('stock-balance')).toHaveAttribute('data-value', '0');

    // find via search
    await page.getByTestId('form-back').click();
    await page.getByTestId('stock-search').fill(code);
    const row = page.getByTestId('stock-table').locator('tbody tr').filter({ hasText: code });
    await expect(row).toHaveCount(1);
    await expect(page.getByTestId('stock-table').locator('tbody tr')).toHaveCount(1);

    // edit name
    await row.getByTestId('row-edit').click();
    await expect(page.getByTestId('stock-name')).toHaveValue('E2E Elma Türü');
    await page.getByTestId('stock-name').fill('E2E Elma Türü 2');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');
    await page.getByTestId('form-back').click();
    await page.getByTestId('stock-search').fill(code);
    await expect(page.getByTestId('stock-table').locator('tbody tr').filter({ hasText: 'E2E Elma Türü 2' })).toHaveCount(1);

    // delete (admin only button) with confirm dialog
    await page.getByTestId('stock-table').locator('tbody tr').filter({ hasText: code }).getByTestId('row-delete').click();
    await expect(page.getByText('Silme Onayı')).toBeVisible();
    await confirmYes(page);
    await expectToast(page, 'Başarılı');
    await expect(page.getByTestId('stock-table').locator('tbody tr').filter({ hasText: code })).toHaveCount(0);
    await expect(page.getByTestId('stock-table')).toContainText('Kayıt bulunamadı.');
  });

  test('stock: validation errors shown under fields; duplicate code → error toast', async ({ page }) => {
    await login(page, 'demo');
    await gotoPage(page, '/stocks/new');
    await page.getByTestId('stock-code').fill('ELMA');
    await page.getByTestId('stock-name').fill('Kopya');
    await selectOption(page, 'stock-unit', 'Kg');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Hata');
    await expect(page).toHaveURL(/\/stocks\/new$/);
  });

  test('admin with "Tüm Firmalar" cannot create (warning, Yeni disabled)', async ({ page }) => {
    await login(page, 'admin');
    await page.getByTestId('nav-stocks').click();
    await expect(page.getByText('Lütfen önce firma seçiniz.')).toBeVisible();
    await expect(page.getByTestId('stock-new')).toBeDisabled();
  });

  test('current account: create → search → edit → delete', async ({ page }) => {
    const code = uniq('E2EC');
    await login(page, 'admin');
    await switchCompany(page);
    await page.getByTestId('nav-current-accounts').click();
    await expect(page.getByTestId('page-title')).toHaveText('Cari Hesaplar');
    await page.getByTestId('account-new').click();
    await page.getByTestId('account-code').fill(code);
    await page.getByTestId('account-title').fill('E2E Müşteri Ltd.');
    await selectOption(page, 'account-type', 'Müşteri');
    await page.getByTestId('account-city').fill('Bursa');
    await page.getByTestId('account-tax-number').fill('1234567890');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');
    await expect(page).toHaveURL(/\/current-accounts\/\d+$/);
    await expect(page.getByTestId('account-balance')).toHaveAttribute('data-value', '0');

    await page.getByTestId('form-back').click();
    await page.getByTestId('account-search').fill(code);
    const row = page.getByTestId('account-table').locator('tbody tr').filter({ hasText: code });
    await expect(row).toHaveCount(1);
    await row.getByTestId('row-edit').click();
    await page.getByTestId('account-title').fill('E2E Müşteri Ltd. 2');
    await selectOption(page, 'account-type', 'Müşteri/Tedarikçi');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');

    await page.getByTestId('form-back').click();
    await page.getByTestId('account-search').fill(code);
    const edited = page.getByTestId('account-table').locator('tbody tr').filter({ hasText: 'E2E Müşteri Ltd. 2' });
    await expect(edited).toHaveCount(1);
    await expect(edited).toContainText('Müşteri/Tedarikçi');
    await edited.getByTestId('row-delete').click();
    await confirmYes(page);
    await expectToast(page, 'Başarılı');
    await expect(page.getByTestId('account-table').locator('tbody tr').filter({ hasText: code })).toHaveCount(0);
  });

  test('operator sees no delete button on stocks', async ({ page }) => {
    await login(page, 'demo');
    await page.getByTestId('nav-stocks').click();
    await expect(page.getByTestId('stock-table').locator('tbody tr').first()).toBeVisible();
    await expect(page.getByTestId('stock-table').getByTestId('row-delete')).toHaveCount(0);
  });

  test('paging & filter: search "EL" shows ELMA only, paginator changes page, unit filter', async ({ page, baseURL }) => {
    const api = await Api.as(baseURL!, 'demo');
    const prefix = uniq('E2EPG');
    for (let i = 0; i < 12; i++) await api.createStock(`${prefix}-${String(i).padStart(2, '0')}`, { name: `Sayfa ${i}` });
    await api.dispose();

    await login(page, 'demo');
    await page.getByTestId('nav-stocks').click();
    await page.getByTestId('stock-search').fill('EL');
    const rows = page.getByTestId('stock-table').locator('tbody tr');
    await expect(rows.filter({ hasText: 'ELMA' })).toHaveCount(1);
    await expect(rows).toHaveCount(1);
    await expect(page).toHaveURL(/q=EL/); // filters synced to the URL query

    await gotoPage(page, `/stocks?q=${prefix}&pageSize=10&sort=code`);
    await expect(rows).toHaveCount(10);
    await expect(rows.first()).toContainText(`${prefix}-00`);
    await page.locator('.p-paginator-next').click();
    await expect(rows).toHaveCount(2);
    await expect(rows.first()).toContainText(`${prefix}-10`);
    await expect(page).toHaveURL(/page=2/);

    await gotoPage(page, '/stocks');
    await selectOption(page, 'stock-filter-unit', 'Kg');
    await expect(rows.filter({ hasText: 'ELMA' })).toHaveCount(1);
    await expect(rows.filter({ hasText: 'EKMEK' })).toHaveCount(0);
  });
});
