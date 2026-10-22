import { expect, test, type Page } from '@playwright/test';
import { gotoPage, Api, confirmYes, dataValue, expectToast, fillNumber, login, logout, pickAutoComplete, switchCompany } from './helpers';

// CONTRACT §11.4 #4, #5: Ahmet/Elma flow in the UI, cancel by admin, insufficient stock toast.

async function stockBalanceInUi(page: Page, code: string) {
  await page.getByTestId('nav-stocks').click();
  await page.getByTestId('stock-search').fill(code);
  const row = page.getByTestId('stock-table').locator('tbody tr').filter({ hasText: code });
  await expect(row).toHaveCount(1);
  await row.getByTestId('row-edit').click();
  return dataValue(page, 'stock-balance');
}

async function accountBalanceInUi(page: Page, code: string) {
  await page.getByTestId('nav-current-accounts').click();
  await page.getByTestId('account-search').fill(code);
  const row = page.getByTestId('account-table').locator('tbody tr').filter({ hasText: code });
  await expect(row).toHaveCount(1);
  await row.getByTestId('row-edit').click();
  return dataValue(page, 'account-balance');
}

test.describe.serial('invoice lifecycle (Ahmet / 2 kg Elma)', () => {
  let invoiceUrl = '';
  let elmaBefore = 0;
  let ahmetBefore = 0;
  let documentNo = '';

  test('operator creates sales invoice with AutoComplete, live totals, approves; balances move @smoke', async ({ page }) => {
    await login(page, 'demo');
    elmaBefore = await stockBalanceInUi(page, 'ELMA');
    ahmetBefore = await accountBalanceInUi(page, 'C001');

    await page.getByTestId('nav-invoices').click();
    await page.getByTestId('invoice-new').click();
    await expect(page.getByTestId('invoice-status')).toContainText('Yeni');
    await expect(page.getByTestId('invoice-type')).toContainText('Satış');
    await pickAutoComplete(page, 'invoice-account', 'C001', /C001 - Ahmet Yılmaz/);
    await pickAutoComplete(page, 'line-0-stock', 'ELMA', /ELMA - Elma/);

    // stock pre-fills price and VAT
    await expect(page.getByTestId('line-0-unit-price').locator('input').first()).toHaveValue(/50/);
    await expect(page.getByTestId('line-0-vat-rate')).toContainText('%1');
    await fillNumber(page, 'line-0-quantity', 2);

    // live totals (same formulas as server)
    await expect(page.getByTestId('line-0-net')).toHaveAttribute('data-value', '100');
    await expect(page.getByTestId('line-0-total')).toHaveAttribute('data-value', '101');
    await expect(page.getByTestId('invoice-subtotal')).toHaveAttribute('data-value', '100');
    await expect(page.getByTestId('invoice-vat-total')).toHaveAttribute('data-value', '1');
    await expect(page.getByTestId('invoice-grand-total')).toHaveAttribute('data-value', '101');

    await page.getByTestId('invoice-save').click();
    await expectToast(page, 'Başarılı');
    await expect(page).toHaveURL(/\/invoices\/\d+$/);
    await expect(page.getByTestId('invoice-status')).toContainText('Taslak');
    await expect(page.getByTestId('invoice-document-no')).toHaveText('—');
    invoiceUrl = new URL(page.url()).pathname;

    await page.getByTestId('invoice-approve').click();
    await expect(page.getByText('Fatura onaylanacak, stok ve cari hareketleri oluşacak. Emin misiniz?')).toBeVisible();
    await confirmYes(page);
    await expect(page.getByTestId('invoice-status')).toContainText('Onaylandı');
    await expect(page.getByTestId('invoice-document-no')).toHaveText(/^SAT\d{6}$/);
    documentNo = (await page.getByTestId('invoice-document-no').textContent())!.trim();
    await expect(page.getByTestId('invoice-approve')).toHaveCount(0); // read-only now
    await expect(page.getByTestId('invoice-grand-total')).toHaveAttribute('data-value', '101');

    // balances reflect on stock / account pages (UI cache invalidated after approve)
    expect(await stockBalanceInUi(page, 'ELMA')).toBe(elmaBefore - 2);
    await page.getByTestId('tab-transactions').click();
    await expect(page.getByTestId('stock-tab-trans-table')).toContainText(documentNo);
    expect(await accountBalanceInUi(page, 'C001')).toBe(ahmetBefore + 101);

    // Stok Hareketleri list shows the Çıkış row
    await page.getByTestId('nav-stock-transactions').click();
    await expect(page.getByTestId('page-title')).toHaveText('Stok Hareketleri');
    const row = page.getByTestId('stock-trans-table').locator('tbody tr').filter({ hasText: documentNo });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Çıkış');
    await expect(row).toContainText('ELMA');

    // Cari Hareketleri shows the Borç row
    await page.getByTestId('nav-account-transactions').click();
    const arow = page.getByTestId('act-trans-table').locator('tbody tr').filter({ hasText: documentNo });
    await expect(arow).toContainText('Borç');
  });

  test('operator has no cancel button; admin cancels with reason → balances restored', async ({ page }) => {
    test.skip(!invoiceUrl, 'needs the approved invoice from the previous test');
    await login(page, 'demo');
    await gotoPage(page, invoiceUrl);
    await expect(page.getByTestId('invoice-status')).toContainText('Onaylandı');
    await expect(page.getByTestId('invoice-cancel')).toHaveCount(0);
    await logout(page);

    await login(page, 'admin');
    await switchCompany(page);
    await gotoPage(page, invoiceUrl);
    await expect(page.getByTestId('invoice-status')).toContainText('Onaylandı');
    await page.getByTestId('invoice-cancel').click();
    await page.getByTestId('cancel-reason').fill('E2E iptal testi');
    await page.getByTestId('cancel-confirm').click();
    await expect(page.getByTestId('invoice-status')).toContainText('İptal Edildi');
    await expect(page.getByTestId('invoice-document-no')).toHaveText(documentNo);

    expect(await stockBalanceInUi(page, 'ELMA')).toBe(elmaBefore);
    expect(await accountBalanceInUi(page, 'C001')).toBe(ahmetBefore);
  });

  test('approving more than the stock balance shows an INSUFFICIENT_STOCK error toast', async ({ page, baseURL }) => {
    const api = await Api.as(baseURL!, 'demo');
    const elma = await api.stockByCode('ELMA');
    await api.dispose();

    await login(page, 'demo');
    await gotoPage(page, '/invoices/new');
    await pickAutoComplete(page, 'invoice-account', 'C002', /C002 - Ayşe Demir/);
    await pickAutoComplete(page, 'line-0-stock', 'ELMA', /ELMA - Elma/);
    await fillNumber(page, 'line-0-quantity', elma.balance + 1000);
    await page.getByTestId('invoice-save').click();
    await expect(page.getByTestId('invoice-status')).toContainText('Taslak');
    await page.getByTestId('invoice-approve').click();
    await confirmYes(page);
    await expectToast(page, 'Hata', /ELMA/);
    await expect(page.getByTestId('invoice-status')).toContainText('Taslak');

    // clean up: delete the draft
    await page.getByTestId('invoice-delete').click();
    await confirmYes(page);
    await expect(page).toHaveURL(/\/invoices$/);
  });

  test('purchase account AutoComplete is filtered by type compatibility; multi-line totals', async ({ page }) => {
    await login(page, 'demo');
    await gotoPage(page, '/invoices/new');
    // Sales: supplier-only S001 must not be offered
    const acc = page.getByTestId('invoice-account').locator('input').first();
    await acc.click();
    await acc.pressSequentially('S001', { delay: 40 });
    await expect(page.getByRole('option', { name: /S001/ })).toHaveCount(0);

    await pickAutoComplete(page, 'line-0-stock', 'DETERJAN', /DETERJAN/);
    await fillNumber(page, 'line-0-quantity', 3);
    await page.getByTestId('invoice-add-line').click();
    await pickAutoComplete(page, 'line-1-stock', 'EKMEK', /EKMEK/);
    await fillNumber(page, 'line-1-quantity', 4);
    // 3 × 250 @20% = 750 + 150 ; 4 × 15 @1% = 60 + 0.60 → 810 / 150.6 / 960.6
    await expect(page.getByTestId('invoice-subtotal')).toHaveAttribute('data-value', '810');
    await expect(page.getByTestId('invoice-vat-total')).toHaveAttribute('data-value', '150.6');
    await expect(page.getByTestId('invoice-grand-total')).toHaveAttribute('data-value', '960.6');
    await page.getByTestId('line-1-remove').click();
    await expect(page.getByTestId('invoice-grand-total')).toHaveAttribute('data-value', '900');
  });
});
