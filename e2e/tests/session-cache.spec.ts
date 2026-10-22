import { expect, test, type Page, type Request } from '@playwright/test';
import { gotoPage, Api, USERS, expectToast, isNuxt, login, selectOption, uniq } from './helpers';

// CONTRACT §11.4 #6 (portal data), #9 (session & token storage), #10 (HTTP cache), #11 (Nuxt SSR).

test.describe('session & token storage', () => {
  test('token never in web storage; sa_refresh httpOnly; reload keeps session; no cookie → login @smoke', async ({ page, context }, testInfo) => {
    await gotoPage(page, '/login');
    await page.getByTestId('login-email').fill(USERS.demo.email);
    await page.getByTestId('login-password').fill(USERS.demo.password);
    const loginResponse = page.waitForResponse((r) => r.url().includes('/api/auth/login') && r.request().method() === 'POST');
    await page.getByTestId('login-submit').click();
    const body = await (await loginResponse).json();
    const token: string = body.accessToken;
    expect(token.length).toBeGreaterThan(20);
    expect(JSON.stringify(body)).not.toMatch(/refresh/i);
    await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');

    const storage = await page.evaluate(() => JSON.stringify({ l: { ...localStorage }, s: { ...sessionStorage } }));
    expect(storage).not.toContain(token);
    expect(storage).not.toContain(token.slice(0, 30));
    expect(storage.toLowerCase()).not.toMatch(/token|refresh|jwt/);
    expect(await page.evaluate(() => document.cookie)).not.toContain('sa_refresh'); // not readable by JS

    const cookies = await context.cookies();
    const refresh = cookies.find((c) => c.name === 'sa_refresh');
    expect(refresh, JSON.stringify(cookies)).toBeTruthy();
    expect(refresh!.httpOnly).toBe(true);
    expect(refresh!.sameSite).toBe('Strict');
    expect(refresh!.path).toBe('/');

    // full reload of /stocks stays logged in (Vue: silent refresh in the browser; Nuxt: SSR refresh)
    await gotoPage(page, '/stocks');
    // let the bootstrap refresh of this navigation finish before reloading (an aborted in-flight refresh could lose the rotated cookie)
    await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
    const refreshCalls: string[] = [];
    page.on('request', (r) => { if (r.url().includes('/api/auth/refresh')) refreshCalls.push(r.method()); });
    await page.reload();
    await expect(page.getByTestId('page-title')).toHaveText('Stoklar');
    await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
    if (!isNuxt(testInfo)) expect(refreshCalls).toContain('POST');
    const after = await page.evaluate(() => JSON.stringify({ l: { ...localStorage }, s: { ...sessionStorage } }));
    expect(after.toLowerCase()).not.toMatch(/token|jwt/);

    // without the cookie the user lands on /login
    await context.clearCookies();
    await page.reload();
    await expect(page).toHaveURL(/\/login/);
  });
});

test.describe('portal data scoping', () => {
  test('portal user sees only his own invoices and balance', async ({ page, baseURL }) => {
    const api = await Api.as(baseURL!, 'demo');
    const stock = await api.createStock();
    const supplier = await api.createAccount('Supplier');
    await api.createApprovedInvoice('Purchase', supplier.id, stock.id, 10);
    const other = await api.createAccount('Customer');
    const otherInvoice = await api.createApprovedInvoice('Sales', other.id, stock.id, 1);
    const c001 = await api.accountByCode('C001');
    const own = await api.createApprovedInvoice('Sales', c001.id, stock.id, 1);
    await api.dispose();
    const portalApi = await Api.as(baseURL!, 'ahmet');
    const me = await portalApi.get('/me/account');
    await portalApi.dispose();

    await login(page, 'ahmet');
    await expect(page).toHaveURL(/\/my-account$/);
    await expect(page.getByTestId('my-balance')).toHaveAttribute('data-value', String(me.balance));
    await expect(page.getByTestId('my-transactions-table')).toBeVisible();
    const invoices = page.getByTestId('my-invoices-table');
    await expect(invoices).toContainText(own.documentNo);
    await expect(invoices).not.toContainText(otherInvoice.documentNo);
    await expect(invoices).not.toContainText('Taslak');
  });
});

test.describe('UI HTTP cache', () => {
  const isStockList = (r: Request) => r.method() === 'GET' && /\/api\/stocks(\?|$)/.test(new URL(r.url()).pathname + new URL(r.url()).search) && !/\/api\/stocks\/\d/.test(r.url());

  async function navTo(page: Page, nav: string, title: string) {
    await page.getByTestId(nav).click();
    await expect(page.getByTestId('page-title')).toHaveText(title);
  }

  test('list is not re-requested within TTL, but is after a mutation @smoke', async ({ page }, testInfo) => {
    await login(page, 'demo');
    await expect(page.getByTestId('page-title')).toHaveText('Gösterge Paneli');
    const listRequests: string[] = [];
    page.on('request', (r) => { if (isStockList(r)) listRequests.push(r.url()); });

    await navTo(page, 'nav-stocks', 'Stoklar');
    await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
    await navTo(page, 'nav-dashboard', 'Gösterge Paneli');
    await navTo(page, 'nav-stocks', 'Stoklar');
    await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
    await navTo(page, 'nav-dashboard', 'Gösterge Paneli');
    await navTo(page, 'nav-stocks', 'Stoklar');
    await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
    expect(listRequests.length, listRequests.join('\n')).toBeLessThanOrEqual(1);
    const before = listRequests.length;

    // mutation → invalidation → returning to the list issues the request again
    const code = uniq();
    await page.getByTestId('stock-new').click();
    await page.getByTestId('stock-code').fill(code);
    await page.getByTestId('stock-name').fill('Cache Test');
    await selectOption(page, 'stock-unit', 'Adet');
    await page.getByTestId('form-save').click();
    await expectToast(page, 'Başarılı');
    await navTo(page, 'nav-stocks', 'Stoklar');
    await expect(page.getByTestId('stock-table')).toBeVisible();
    await expect.poll(() => listRequests.length).toBeGreaterThan(before);
    await page.getByTestId('stock-search').fill(code);
    await expect(page.getByTestId('stock-table')).toContainText(code);

    // "Yenile" bypasses the cache
    const n = listRequests.length;
    await page.getByTestId('stock-refresh').click();
    await expect.poll(() => listRequests.length).toBeGreaterThan(n);
    void testInfo;
  });

  test('Nuxt: list page loaded by SSR makes no browser request for its data', async ({ page }, testInfo) => {
    test.skip(!isNuxt(testInfo), 'Nuxt only');
    await login(page, 'demo');
    const listRequests: string[] = [];
    page.on('request', (r) => { if (isStockList(r)) listRequests.push(r.url()); });
    await gotoPage(page, '/stocks');
    await expect(page.getByTestId('stock-table')).toContainText('ARMUT');
    await page.waitForLoadState('networkidle');
    expect(listRequests, listRequests.join('\n')).toHaveLength(0);
  });
});

test.describe('Nuxt SSR', () => {
  test('page HTML is server-rendered with data @smoke', async ({ page, request }, testInfo) => {
    test.skip(!isNuxt(testInfo), 'Nuxt only');
    const loginHtml = await (await request.get('/login')).text();
    expect(loginHtml).toContain('login-email');

    await login(page, 'demo');
    const res = await page.request.get('/stocks');
    expect(res.status()).toBe(200);
    const html = await res.text();
    expect(html).toContain('data-testid="stock-table"');
    expect(html).toContain('ARMUT'); // first page sorted by code (ELMA may be on page 2 after many E2E runs)
    expect(html).toContain('Stoklar');
    // anonymous SSR request for a guarded page → 302 to /login
    const anon = await request.get('/stocks', { maxRedirects: 0 });
    expect([301, 302, 303, 307]).toContain(anon.status());
    expect(anon.headers()['location']).toMatch(/\/login/);
  });
});
