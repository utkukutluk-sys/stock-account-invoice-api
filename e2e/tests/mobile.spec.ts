import { expect, test, type Page } from '@playwright/test';
import { fillNumber, gotoPage, hydrated, login, pickAutoComplete } from './helpers';

// Mobile viewport smoke (projects vue-mobile / nuxt-mobile, Pixel 7): CONTRACT §10.1 responsive sidebar < 992 px.

async function noHorizontalOverflow(page: Page) {
  const { scroll, width } = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, width: window.innerWidth }));
  expect(scroll, `page scrolls horizontally (${scroll} > ${width})`).toBeLessThanOrEqual(width + 1);
}

async function openMenu(page: Page) {
  await hydrated(page);
  await page.getByRole('button', { name: 'Menü' }).click();
}

test('mobile: login, collapsed menu, list, invoice editor @smoke', async ({ page }) => {
  await gotoPage(page, '/login');
  await noHorizontalOverflow(page);
  await login(page, 'demo');
  await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');
  await noHorizontalOverflow(page);

  // sidebar is collapsed below 992 px and opens via the top-bar toggle
  await expect(page.getByTestId('nav-stocks')).not.toBeInViewport();
  await openMenu(page);
  await expect(page.getByTestId('nav-stocks')).toBeInViewport();
  await page.getByTestId('nav-stocks').click();
  await expect(page.getByTestId('page-title')).toHaveText('Stoklar');
  await expect(page.getByTestId('nav-stocks')).not.toBeInViewport(); // closes after navigation
  await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
  await page.getByTestId('stock-search').fill('ELMA');
  await expect(page.getByTestId('stock-table').locator('tbody tr').filter({ hasText: 'ELMA' })).toHaveCount(1);
  await noHorizontalOverflow(page);

  // invoice editor works on a phone: AutoCompletes, quantity, live totals
  await gotoPage(page, '/invoices/new');
  await pickAutoComplete(page, 'invoice-account', 'C001', /C001 - Ahmet Yılmaz/);
  await pickAutoComplete(page, 'line-0-stock', 'ELMA', /ELMA - Elma/);
  await fillNumber(page, 'line-0-quantity', 2);
  await expect(page.getByTestId('invoice-grand-total')).toHaveAttribute('data-value', '101');
  await expect(page.getByTestId('invoice-save')).toBeVisible();
  await noHorizontalOverflow(page);
});
