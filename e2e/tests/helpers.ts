import { expect, request, type APIRequestContext, type Page, type TestInfo } from '@playwright/test';

export const USERS = {
  admin: { email: 'admin@stockaccount.local', password: 'Admin123!' },
  demo: { email: 'demo@stockaccount.local', password: 'Demo123!' },
  ahmet: { email: 'ahmet@stockaccount.local', password: 'Ahmet123!' },
} as const;
export type UserKey = keyof typeof USERS;

export const DEMO_COMPANY = 'Demo Gıda A.Ş.';

let seq = 0;
/** Unique, contract-compliant code (`E2E{timestamp}`), re-runnable. */
export const uniq = (prefix = 'E2E') => `${prefix}${Date.now().toString().slice(-8)}${(seq++ % 100).toString().padStart(2, '0')}`;

export const isNuxt = (testInfo: TestInfo) => testInfo.project.name === 'nuxt';

/** Nuxt keeps controls in <fieldset disabled> until hydration; Vue has none → resolves immediately. */
export async function hydrated(page: Page) {
  await page.waitForFunction(() => !document.querySelector('fieldset[disabled]'), undefined, { timeout: 30_000 });
}

export async function gotoPage(page: Page, url: string) {
  await page.goto(url);
  await hydrated(page);
}

export async function login(page: Page, user: UserKey) {
  await gotoPage(page, '/login');
  await page.getByTestId('login-email').fill(USERS[user].email);
  await page.getByTestId('login-password').fill(USERS[user].password);
  await page.getByTestId('login-submit').click();
  await expect(page).not.toHaveURL(/\/login/);
  await expect(page.getByTestId('logout-button')).toBeVisible();
}

export async function logout(page: Page) {
  await hydrated(page);
  await page.getByTestId('logout-button').click();
  await expect(page).toHaveURL(/\/login/, { timeout: 30_000 }); // nuxt dev compiles /login on first use
}

/** Opens a composite Select by its wrapper and picks an option. */
export async function selectOption(page: Page, testid: string, name: string | RegExp) {
  await hydrated(page);
  await page.getByTestId(testid).click();
  await page.getByRole('option', { name, exact: typeof name === 'string' }).click();
}

export async function switchCompany(page: Page, name = DEMO_COMPANY) {
  await selectOption(page, 'company-switcher', name);
  await expect(page.getByTestId('company-switcher')).toContainText(name);
}

/** Types into a composite (InputNumber/AutoComplete) wrapper's inner input. */
export function innerInput(page: Page, testid: string) {
  return page.getByTestId(testid).locator('input').first();
}

export async function fillNumber(page: Page, testid: string, value: number | string) {
  const input = innerInput(page, testid);
  await input.click();
  await input.press('Control+a');
  await input.pressSequentially(String(value).replace('.', ','));
  await input.press('Tab');
}

export async function pickAutoComplete(page: Page, testid: string, typed: string, option: string | RegExp) {
  await hydrated(page);
  const input = innerInput(page, testid);
  await input.click();
  await input.fill('');
  await input.pressSequentially(typed, { delay: 40 });
  await page.getByRole('option', { name: option }).first().click();
}

export async function dataValue(page: Page, testid: string): Promise<number> {
  const el = page.getByTestId(testid);
  await expect(el).toHaveAttribute('data-value', /.+/);
  return Number(await el.getAttribute('data-value'));
}

export async function expectToast(page: Page, summary: 'Başarılı' | 'Hata', detail?: string | RegExp) {
  const toast = page.locator('.p-toast-message').filter({ hasText: summary }).last();
  await expect(toast).toBeVisible();
  if (detail) await expect(toast).toContainText(detail);
}

export async function confirmYes(page: Page) {
  await page.getByRole('button', { name: 'Evet', exact: true }).click();
}

// ---------- direct API access (through the UI origin's /api proxy) for setup and cross-checks ----------
export class Api {
  private constructor(private readonly ctx: APIRequestContext, private readonly token: string, private readonly companyId?: number) {}

  static async as(baseURL: string, user: UserKey, companyId?: number) {
    const ctx = await request.newContext({ baseURL });
    const res = await ctx.post('/api/auth/login', { data: USERS[user] });
    expect(res.status(), await res.text()).toBe(200);
    const body = await res.json();
    return new Api(ctx, body.accessToken as string, companyId);
  }

  private headers() {
    return { Authorization: `Bearer ${this.token}`, ...(this.companyId ? { 'X-Company-Id': String(this.companyId) } : {}) };
  }

  async get(url: string) {
    const res = await this.ctx.get(`/api${url}`, { headers: this.headers() });
    expect(res.status(), `${url}: ${await res.text()}`).toBe(200);
    return res.json();
  }

  async post(url: string, data: unknown, expected = 200) {
    const res = await this.ctx.post(`/api${url}`, { headers: this.headers(), data });
    expect(res.status(), `${url}: ${await res.text()}`).toBe(expected);
    return res.json();
  }

  async stockByCode(code: string) {
    const r = await this.get(`/stocks?q=${encodeURIComponent(code)}`);
    return r.items.find((s: { code: string }) => s.code === code);
  }

  async accountByCode(code: string) {
    const r = await this.get(`/current-accounts?q=${encodeURIComponent(code)}`);
    return r.items.find((s: { code: string }) => s.code === code);
  }

  async createStock(code = uniq(), extra: Record<string, unknown> = {}) {
    return this.post('/stocks', { code, name: `Stok ${code}`, unit: 'Piece', vatRate: 20, unitPrice: 10, isActive: true, ...extra }, 201);
  }

  async createAccount(type = 'Customer', code = uniq('E2EC'), title = `Cari ${code}`) {
    return this.post('/current-accounts', { code, title, type, isActive: true }, 201);
  }

  async createApprovedInvoice(type: 'Sales' | 'Purchase', accountId: number, stockId: number, quantity: number, unitPrice = 10, vatRate = 20) {
    const today = new Date().toISOString().slice(0, 10);
    const inv = await this.post('/invoices', { type, invoiceDate: today, currentAccountId: accountId, lines: [{ stockId, quantity, unitPrice, vatRate }] }, 201);
    return this.post(`/invoices/${inv.id}/approve`, { rowVersion: inv.rowVersion });
  }

  async dispose() {
    await this.ctx.dispose();
  }
}
