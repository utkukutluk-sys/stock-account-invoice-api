// Generates StockAccount.postman_collection.json (Postman v2.1) from a compact definition.
// Run: node build-collection.mjs   (the generated JSON is committed; edit this file, not the JSON)
import { writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';

const lines = (...l) => l.flat();

// ---------- shared test snippets ----------
const status = (code) => `pm.test('status ${code}', () => pm.response.to.have.status(${code}));`;
const problem = (code, st) => lines(
  status(st),
  `pm.test('problem+json ${code}', () => {`,
  `  pm.expect(pm.response.headers.get('Content-Type')).to.include('application/problem+json');`,
  `  const b = pm.response.json();`,
  `  pm.expect(b.code).to.eql('${code}');`,
  `  pm.expect(b.status).to.eql(${st});`,
  `  pm.expect(b.correlationId, 'correlationId').to.be.a('string').and.not.empty;`,
  `});`,
);
const paged = () => lines(
  `pm.test('PagedResult envelope', () => {`,
  `  const b = pm.response.json();`,
  `  pm.expect(b).to.have.all.keys('items','page','pageSize','totalCount','totalPages');`,
  `  pm.expect(b.items).to.be.an('array');`,
  `});`,
);
const xcache = (v) => `pm.test('X-Cache ${v}', () => pm.expect(pm.response.headers.get('X-Cache')).to.eql('${v}'));`;
const save = (varName, expr) => `pm.collectionVariables.set('${varName}', ${expr});`;

// ---------- request builder ----------
function req(name, method, path, opt = {}) {
  const headers = [];
  if (opt.body !== undefined) headers.push({ key: 'Content-Type', value: 'application/json' });
  if (opt.company) headers.push({ key: 'X-Company-Id', value: opt.company });
  for (const [k, v] of Object.entries(opt.headers ?? {})) headers.push({ key: k, value: v });

  const raw = (opt.host ?? '{{baseUrl}}') + path;
  const [base, query] = raw.split('?');
  const item = {
    name,
    request: {
      method,
      header: headers,
      url: {
        raw,
        host: [base.split('/')[0] === '{{baseUrl}}' || base.startsWith('{{') ? base.match(/^\{\{[^}]+\}\}/)[0] : base],
        path: base.replace(/^\{\{[^}]+\}\}\/?/, '').split('/').filter(Boolean),
        ...(query ? { query: query.split('&').map((p) => { const [key, value = ''] = p.split('='); return { key, value }; }) } : {}),
      },
      ...(opt.description ? { description: opt.description } : {}),
    },
    event: [],
  };
  if (opt.body !== undefined) {
    // numeric id placeholders must not be quoted: "{{customerId}}" → {{customerId}}
    const raw = (typeof opt.body === 'string' ? opt.body : JSON.stringify(opt.body, null, 2)).replace(/"\{\{(\w*Id)\}\}"/g, '{{$1}}');
    item.request.body = { mode: 'raw', raw, options: { raw: { language: 'json' } } };
  }
  if (opt.auth === 'none') item.request.auth = { type: 'noauth' };
  else if (opt.auth) item.request.auth = { type: 'bearer', bearer: [{ key: 'token', value: `{{${opt.auth}}}`, type: 'string' }] };
  if (opt.pre) item.event.push({ listen: 'prerequest', script: { type: 'text/javascript', exec: lines(opt.pre) } });
  if (opt.tests) item.event.push({ listen: 'test', script: { type: 'text/javascript', exec: lines(opt.tests) } });
  return item;
}
const folder = (name, description, item, extra = {}) => ({ name, description, item, ...extra });

// ---------- collection-level scripts ----------
const collectionPre = [
  "// Logs in as admin when accessToken is empty or about to expire. The refresh token stays in Postman's cookie jar (httpOnly sa_refresh).",
  "const token = pm.collectionVariables.get('accessToken');",
  "const exp = Date.parse(pm.collectionVariables.get('accessTokenExpiresAt') || '') || 0;",
  "if (!token || exp - Date.now() < 60000) {",
  "  pm.sendRequest({",
  "    url: pm.environment.get('baseUrl') + '/auth/login', method: 'POST',",
  "    header: { 'Content-Type': 'application/json' },",
  "    body: { mode: 'raw', raw: JSON.stringify({ email: pm.environment.get('adminEmail'), password: pm.environment.get('adminPassword') }) }",
  "  }, (err, res) => {",
  "    if (err || res.code !== 200) { console.error('auto-login failed', err || res.code); return; }",
  "    const b = res.json();",
  "    pm.collectionVariables.set('accessToken', b.accessToken);",
  "    pm.collectionVariables.set('adminToken', b.accessToken);",
  "    pm.collectionVariables.set('accessTokenExpiresAt', b.accessTokenExpiresAt);",
  "  });",
  "}",
];
const collectionTests = [
  "pm.test('X-Correlation-Id echoed', () => pm.expect(pm.response.headers.has('X-Correlation-Id')).to.be.true);",
];

const unique = (v, prefix) => `pm.collectionVariables.set('${v}', '${prefix}' + Date.now().toString().slice(-9) + Math.floor(Math.random()*90+10));`;
const tax = (v) => `pm.collectionVariables.set('${v}', String(Math.floor(1000000000 + Math.random() * 8999999999)));`;
const today = `pm.collectionVariables.set('today', new Date().toISOString().slice(0,10));`;

// ---------- folders ----------
const auth = folder('Auth', 'Login / cookie refresh rotation / logout. Tokens for the three roles are captured here.', [
  req('Login - invalid credentials', 'POST', '/auth/login', { auth: 'none', body: { email: '{{adminEmail}}', password: 'Wrong-Password1' }, tests: problem('INVALID_CREDENTIALS', 401) }),
  req('Login - validation error', 'POST', '/auth/login', { auth: 'none', body: { email: '', password: '' }, tests: problem('VALIDATION_FAILED', 400) }),
  req('Login - operator (demo)', 'POST', '/auth/login', {
    auth: 'none', body: { email: '{{operatorEmail}}', password: '{{operatorPassword}}' },
    tests: [status(200),
      "const b = pm.response.json();",
      "pm.test('no refresh token in body', () => pm.expect(JSON.stringify(b).toLowerCase()).not.to.include('refreshtoken'));",
      "pm.test('sa_refresh cookie set (httpOnly, SameSite=Strict, Path=/)', () => { const sc = pm.response.headers.all().filter(h => h.key.toLowerCase()==='set-cookie').map(h=>h.value).join('\\n'); pm.expect(sc).to.include('sa_refresh='); pm.expect(sc.toLowerCase()).to.include('httponly'); pm.expect(sc.toLowerCase()).to.include('samesite=strict'); pm.expect(sc.toLowerCase()).to.include('path=/'); });",
      "pm.test('operator user', () => { pm.expect(b.user.role).to.eql('User'); pm.expect(b.user.isPortalUser).to.be.false; pm.expect(b.user.companyId).to.be.a('number'); });",
      save('operatorToken', 'b.accessToken'), save('demoCompanyId', 'b.user.companyId')],
  }),
  req('Login - portal (ahmet)', 'POST', '/auth/login', {
    auth: 'none', body: { email: '{{portalEmail}}', password: '{{portalPassword}}' },
    tests: [status(200), "const b = pm.response.json();", "pm.test('portal user', () => { pm.expect(b.user.isPortalUser).to.be.true; pm.expect(b.user.currentAccountTitle).to.eql('Ahmet Yılmaz'); });", save('portalToken', 'b.accessToken')],
  }),
  req('Login - admin', 'POST', '/auth/login', {
    auth: 'none', body: { email: '{{adminEmail}}', password: '{{adminPassword}}' },
    tests: [status(200), "const b = pm.response.json();", "pm.test('admin without company', () => { pm.expect(b.user.role).to.eql('Admin'); pm.expect(b.user.companyId).to.be.null; });",
      save('accessToken', 'b.accessToken'), save('adminToken', 'b.accessToken'), save('accessTokenExpiresAt', 'b.accessTokenExpiresAt'), save('adminId', 'b.user.id')],
  }),
  req('Me', 'GET', '/auth/me', { tests: [status(200), "pm.test('admin email', () => pm.expect(pm.response.json().email).to.eql(pm.environment.get('adminEmail')));"] }),
  req('Me - anonymous → 401', 'GET', '/auth/me', { auth: 'none', tests: problem('UNAUTHORIZED', 401) }),
  req('Refresh (cookie jar) → rotated', 'POST', '/auth/refresh', {
    auth: 'none', body: {},
    tests: [status(200), "const b = pm.response.json();",
      "pm.test('new access token, no refresh token in body', () => { pm.expect(b.accessToken).to.be.a('string'); pm.expect(b).not.to.have.property('refreshToken'); });",
      "pm.test('rotated cookie', () => pm.expect(pm.response.headers.all().some(h => h.key.toLowerCase()==='set-cookie' && h.value.startsWith('sa_refresh='))).to.be.true);",
      save('accessToken', 'b.accessToken'), save('adminToken', 'b.accessToken'), save('accessTokenExpiresAt', 'b.accessTokenExpiresAt')],
  }),
  req('Refresh - wrong content type → 415', 'POST', '/auth/refresh', { auth: 'none', headers: { 'Content-Type': 'text/plain' }, tests: status(415) }),
  req('Logout → 204', 'POST', '/auth/logout', { auth: 'none', body: {}, tests: status(204) }),
  req('Refresh after logout → 401', 'POST', '/auth/refresh', { auth: 'none', body: {}, tests: problem('INVALID_REFRESH_TOKEN', 401) }),
  req('Login - admin (again)', 'POST', '/auth/login', {
    auth: 'none', body: { email: '{{adminEmail}}', password: '{{adminPassword}}' },
    tests: [status(200), "const b = pm.response.json();", save('accessToken', 'b.accessToken'), save('adminToken', 'b.accessToken'), save('accessTokenExpiresAt', 'b.accessTokenExpiresAt')],
  }),
]);

const companies = folder('Companies', 'AdminOnly CRUD (+ /companies/current for operators).', [
  req('List companies (q=Demo)', 'GET', '/companies?q=Demo&sort=name', { tests: [status(200), ...paged(), "pm.test('Demo company', () => pm.expect(pm.response.json().items.map(c=>c.name)).to.include('Demo Gıda A.Ş.'));"] }),
  req('List companies as operator → 403', 'GET', '/companies', { auth: 'operatorToken', tests: problem('FORBIDDEN', 403) }),
  req('Create company', 'POST', '/companies', {
    pre: [tax('companyTax')], body: { name: 'Postman Firma', taxNumber: '{{companyTax}}', taxOffice: 'Kadıköy', city: 'İzmir', isActive: true },
    tests: [status(201), "pm.test('Location header', () => pm.expect(pm.response.headers.get('Location')).to.be.a('string'));", "const b = pm.response.json();", save('companyId', 'b.id'), save('companyRowVersion', 'b.rowVersion')],
  }),
  req('Create company - duplicate tax number → 409', 'POST', '/companies', { body: { name: 'Dup', taxNumber: '{{companyTax}}', isActive: true }, tests: problem('DUPLICATE_TAX_NUMBER', 409) }),
  req('Create company - validation → 400', 'POST', '/companies', { body: { name: '', taxNumber: '12', email: 'x', isActive: true }, tests: [...problem('VALIDATION_FAILED', 400), "pm.test('errors.taxNumber', () => pm.expect(pm.response.json().errors).to.have.property('taxNumber'));"] }),
  req('Get company', 'GET', '/companies/{{companyId}}', { tests: [status(200), "pm.test('name', () => pm.expect(pm.response.json().name).to.eql('Postman Firma'));", "pm.test('weak ETag', () => pm.expect(pm.response.headers.get('ETag')).to.match(/^W\\/\"[0-9a-f]{16}\"$/));"] }),
  req('Update company', 'PUT', '/companies/{{companyId}}', { body: { name: 'Postman Firma 2', taxNumber: '{{companyTax}}', city: 'İzmir', isActive: true, rowVersion: '{{companyRowVersion}}' }, tests: [status(200), "pm.test('updated', () => pm.expect(pm.response.json().name).to.eql('Postman Firma 2'));"] }),
  req('Update company - stale rowVersion → 409', 'PUT', '/companies/{{companyId}}', { body: { name: 'X', taxNumber: '{{companyTax}}', isActive: true, rowVersion: '{{companyRowVersion}}' }, tests: problem('CONCURRENCY_CONFLICT', 409) }),
  req('Delete Demo company → 409 COMPANY_NOT_EMPTY', 'DELETE', '/companies/{{demoCompanyId}}', { tests: problem('COMPANY_NOT_EMPTY', 409) }),
  req('Delete company', 'DELETE', '/companies/{{companyId}}', { tests: status(204) }),
  req('Get deleted company → 404', 'GET', '/companies/{{companyId}}', { tests: problem('NOT_FOUND', 404) }),
]);

const users = folder('Users', 'AdminOnly user management.', [
  req('Create user (operator of Demo)', 'POST', '/users', {
    pre: [unique('userCode', 'pm')], body: { email: '{{userCode}}@postman.local', fullName: 'Postman Kullanıcı', password: 'Passw0rdPm', role: 'User', companyId: '{{demoCompanyId}}', isActive: true },
    tests: [status(201), "const b = pm.response.json();", "pm.test('company name', () => pm.expect(b.companyName).to.eql('Demo Gıda A.Ş.'));", save('userId', 'b.id')],
  }),
  req('Create user - duplicate email → 409', 'POST', '/users', { body: { email: '{{userCode}}@postman.local', fullName: 'X', password: 'Passw0rdPm', role: 'User', companyId: '{{demoCompanyId}}', isActive: true }, tests: problem('DUPLICATE_EMAIL', 409) }),
  req('Create user - admin with company → 400', 'POST', '/users', { body: { email: 'x{{userCode}}@postman.local', fullName: 'X', password: 'Passw0rdPm', role: 'Admin', companyId: '{{demoCompanyId}}', isActive: true }, tests: problem('VALIDATION_FAILED', 400) }),
  req('List users (q, role)', 'GET', '/users?q={{userCode}}&role=User&sort=-createdAt', { tests: [status(200), ...paged(), "pm.test('one match', () => pm.expect(pm.response.json().totalCount).to.eql(1));"] }),
  req('Update user', 'PUT', '/users/{{userId}}', { body: { fullName: 'Postman Kullanıcı 2', role: 'User', companyId: '{{demoCompanyId}}', isActive: true }, tests: [status(200), "pm.test('updated', () => pm.expect(pm.response.json().fullName).to.eql('Postman Kullanıcı 2'));"] }),
  req('Delete user', 'DELETE', '/users/{{userId}}', { tests: status(204) }),
  req('Delete self → 409', 'DELETE', '/users/{{adminId}}', { tests: problem('CANNOT_DELETE_SELF', 409) }),
]);

const stockBody = (extra = {}) => ({ code: '{{stockCode}}', name: 'Postman Stok', unit: 'Kg', vatRate: 1, unitPrice: 12.5, isActive: true, ...extra });
const stocks = folder('Stocks', 'Operator CRUD, paging/sort/filter, Redis cache (X-Cache) and ETag. Delete is AdminOnly.', [
  req('Create stock', 'POST', '/stocks', {
    auth: 'operatorToken', pre: [unique('stockCode', 'PM')], body: { ...stockBody(), code: '{{stockCode}}' },
    tests: [status(201), "const b = pm.response.json();", "pm.test('balance 0, system maintained', () => pm.expect(b.balance).to.eql(0));", "pm.test('code normalized', () => pm.expect(b.code).to.eql(pm.collectionVariables.get('stockCode')));", save('stockId', 'b.id'), save('stockRowVersion', 'b.rowVersion')],
  }),
  req('Create stock - duplicate code → 409', 'POST', '/stocks', { auth: 'operatorToken', body: stockBody(), tests: problem('DUPLICATE_CODE', 409) }),
  req('Create stock - validation → 400', 'POST', '/stocks', { auth: 'operatorToken', body: { code: 'bad code!', name: '', unit: 'Kg', vatRate: 7, unitPrice: -1, isActive: true }, tests: [...problem('VALIDATION_FAILED', 400), "pm.test('errors.vatRate', () => pm.expect(pm.response.json().errors).to.have.property('vatRate'));"] }),
  req('Create stock as admin without company → 400', 'POST', '/stocks', { body: stockBody({ code: 'X{{stockCode}}' }), tests: problem('COMPANY_REQUIRED', 400) }),
  req('Get stock → X-Cache MISS', 'GET', '/stocks/{{stockId}}', { auth: 'operatorToken', tests: [status(200), xcache('MISS')] }),
  req('Get stock → X-Cache HIT + ETag', 'GET', '/stocks/{{stockId}}', { auth: 'operatorToken', tests: [status(200), xcache('HIT'), save('stockEtag', "pm.response.headers.get('ETag')")] }),
  req('Get stock If-None-Match → 304', 'GET', '/stocks/{{stockId}}', { auth: 'operatorToken', headers: { 'If-None-Match': '{{stockEtag}}' }, tests: [status(304), "pm.test('empty body', () => pm.expect(pm.response.text()).to.eql(''));"] }),
  req('List stocks (q, sort, paging)', 'GET', '/stocks?q=PM&sort=-createdAt,code&page=1&pageSize=5', { auth: 'operatorToken', tests: [status(200), ...paged(), "pm.test('pageSize 5', () => pm.expect(pm.response.json().pageSize).to.eql(5));", "pm.test('X-Cache header', () => pm.expect(['HIT','MISS']).to.include(pm.response.headers.get('X-Cache')));"] }),
  req('List stocks filter unit=Kg&isActive=true', 'GET', '/stocks?unit=Kg&isActive=true', { auth: 'operatorToken', tests: [status(200), "pm.test('all Kg', () => pm.response.json().items.forEach(s => pm.expect(s.unit).to.eql('Kg')));"] }),
  req('List stocks pageSize=101 → 400', 'GET', '/stocks?pageSize=101', { auth: 'operatorToken', tests: problem('VALIDATION_FAILED', 400) }),
  req('List stocks unknown sort → 400', 'GET', '/stocks?sort=hacked', { auth: 'operatorToken', tests: [...problem('VALIDATION_FAILED', 400), "pm.test('errors.sort', () => pm.expect(pm.response.json().errors).to.have.property('sort'));"] }),
  req('Update stock', 'PUT', '/stocks/{{stockId}}', { auth: 'operatorToken', body: stockBody({ name: 'Postman Stok 2', rowVersion: '{{stockRowVersion}}' }), tests: [status(200), "const b = pm.response.json();", "pm.test('renamed', () => pm.expect(b.name).to.eql('Postman Stok 2'));", save('stockRowVersion2', 'b.rowVersion')] }),
  req('Get stock after update → MISS, fresh', 'GET', '/stocks/{{stockId}}', { auth: 'operatorToken', tests: [status(200), xcache('MISS'), "pm.test('fresh name', () => pm.expect(pm.response.json().name).to.eql('Postman Stok 2'));"] }),
  req('Update stock - stale rowVersion → 409', 'PUT', '/stocks/{{stockId}}', { auth: 'operatorToken', body: stockBody({ rowVersion: '{{stockRowVersion}}' }), tests: problem('CONCURRENCY_CONFLICT', 409) }),
  req('Delete stock as operator → 403', 'DELETE', '/stocks/{{stockId}}', { auth: 'operatorToken', tests: problem('FORBIDDEN', 403) }),
  req('Delete stock as admin', 'DELETE', '/stocks/{{stockId}}', { company: '{{demoCompanyId}}', tests: status(204) }),
  req('Get deleted stock → 404', 'GET', '/stocks/{{stockId}}', { auth: 'operatorToken', tests: problem('NOT_FOUND', 404) }),
]);

const accounts = folder('CurrentAccounts', 'Operator CRUD of current accounts (cari). Delete is AdminOnly.', [
  req('Create account', 'POST', '/current-accounts', {
    auth: 'operatorToken', pre: [unique('accountCode', 'PMC')], body: { code: '{{accountCode}}', title: 'Postman Cari', type: 'Customer', city: 'Ankara', taxNumber: '1234567890', isActive: true },
    tests: [status(201), "const b = pm.response.json();", "pm.test('type string', () => pm.expect(b.type).to.eql('Customer'));", save('accountId', 'b.id'), save('accountRowVersion', 'b.rowVersion')],
  }),
  req('Create account - duplicate → 409', 'POST', '/current-accounts', { auth: 'operatorToken', body: { code: '{{accountCode}}', title: 'Dup', type: 'Customer', isActive: true }, tests: problem('DUPLICATE_CODE', 409) }),
  req('Get account → MISS', 'GET', '/current-accounts/{{accountId}}', { auth: 'operatorToken', tests: [status(200), xcache('MISS')] }),
  req('Get account → HIT', 'GET', '/current-accounts/{{accountId}}', { auth: 'operatorToken', tests: [status(200), xcache('HIT')] }),
  req('List accounts (type, city, q)', 'GET', '/current-accounts?type=Customer&city=Ankara&q={{accountCode}}', { auth: 'operatorToken', tests: [status(200), ...paged(), "pm.test('one match', () => pm.expect(pm.response.json().totalCount).to.eql(1));"] }),
  req('Update account', 'PUT', '/current-accounts/{{accountId}}', { auth: 'operatorToken', body: { code: '{{accountCode}}', title: 'Postman Cari 2', type: 'Both', city: 'Ankara', isActive: true, rowVersion: '{{accountRowVersion}}' }, tests: [status(200), "pm.test('updated', () => pm.expect(pm.response.json().type).to.eql('Both'));"] }),
  req('Delete account as admin', 'DELETE', '/current-accounts/{{accountId}}', { company: '{{demoCompanyId}}', tests: status(204) }),
  req('Get deleted account → 404', 'GET', '/current-accounts/{{accountId}}', { auth: 'operatorToken', tests: problem('NOT_FOUND', 404) }),
]);

const line = (qty, price, vat, stock = '{{invStockId}}') => ({ stockId: stock, quantity: qty, unitPrice: price, vatRate: vat });
const invoiceBody = (type, account, lns, extra = {}) => ({ type, invoiceDate: '{{today}}', currentAccountId: account, lines: lns, ...extra });
const invoices = folder('Invoices', 'Draft CRUD, approve (numbering, ledgers), INSUFFICIENT_STOCK, type mismatch, concurrency, cancel (admin).', [
  req('Setup: stock', 'POST', '/stocks', { auth: 'operatorToken', pre: [unique('invStockCode', 'PMI'), today], body: { code: '{{invStockCode}}', name: 'Fatura Stok', unit: 'Piece', vatRate: 20, unitPrice: 10, isActive: true }, tests: [status(201), save('invStockId', 'pm.response.json().id')] }),
  req('Setup: supplier', 'POST', '/current-accounts', { auth: 'operatorToken', pre: [unique('supCode', 'PMS')], body: { code: '{{supCode}}', title: 'Postman Tedarikçi', type: 'Supplier', isActive: true }, tests: [status(201), save('supplierId', 'pm.response.json().id')] }),
  req('Setup: customer', 'POST', '/current-accounts', { auth: 'operatorToken', pre: [unique('cusCode', 'PMK')], body: { code: '{{cusCode}}', title: 'Postman Müşteri', type: 'Customer', isActive: true }, tests: [status(201), save('customerId', 'pm.response.json().id')] }),
  req('Create purchase draft (10 × 10 @20%)', 'POST', '/invoices', {
    auth: 'operatorToken', body: invoiceBody('Purchase', '{{supplierId}}', [line(10, 10, 20)]),
    tests: [status(201), "const b = pm.response.json();",
      "pm.test('Draft, ALS, totals 100/20/120, no number', () => { pm.expect(b.status).to.eql('Draft'); pm.expect(b.series).to.eql('ALS'); pm.expect(b.subTotal).to.eql(100); pm.expect(b.vatTotal).to.eql(20); pm.expect(b.grandTotal).to.eql(120); pm.expect(b.number).to.be.null; pm.expect(b.documentNo).to.be.null; });",
      save('purchaseId', 'b.id'), save('purchaseRowVersion', 'b.rowVersion')],
  }),
  req('Approve purchase → ALS number, stock In', 'POST', '/invoices/{{purchaseId}}/approve', { auth: 'operatorToken', body: { rowVersion: '{{purchaseRowVersion}}' }, tests: [status(200), "const b = pm.response.json();", "pm.test('Approved ALSnnnnnn', () => { pm.expect(b.status).to.eql('Approved'); pm.expect(b.documentNo).to.match(/^ALS\\d{6}$/); });"] }),
  req('Stock balance after purchase = 10', 'GET', '/stocks/{{invStockId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('balance 10', () => pm.expect(pm.response.json().balance).to.eql(10));"] }),
  req('Purchase with customer-only account → 422', 'POST', '/invoices', { auth: 'operatorToken', body: invoiceBody('Purchase', '{{customerId}}', [line(1, 1, 0)]), tests: problem('ACCOUNT_TYPE_MISMATCH', 422) }),
  req('Create invoice - invalid lines → 400', 'POST', '/invoices', { auth: 'operatorToken', body: invoiceBody('Sales', '{{customerId}}', [line(0, -1, 7)]), tests: [...problem('VALIDATION_FAILED', 400), "pm.test('errors lines[0].quantity', () => pm.expect(pm.response.json().errors).to.have.property('lines[0].quantity'));"] }),
  req('Create invoice - no lines → 400', 'POST', '/invoices', { auth: 'operatorToken', body: invoiceBody('Sales', '{{customerId}}', []), tests: problem('VALIDATION_FAILED', 400) }),
  req('Create sales draft (qty 12 > balance 10)', 'POST', '/invoices', { auth: 'operatorToken', body: invoiceBody('Sales', '{{customerId}}', [line(12, 15, 20)]), tests: [status(201), "const b = pm.response.json();", save('saleId', 'b.id'), save('saleRowVersion', 'b.rowVersion')] }),
  req('Approve → 422 INSUFFICIENT_STOCK', 'POST', '/invoices/{{saleId}}/approve', { auth: 'operatorToken', body: { rowVersion: '{{saleRowVersion}}' }, tests: [...problem('INSUFFICIENT_STOCK', 422), "pm.test('detail lists code', () => pm.expect(pm.response.json().detail).to.include(pm.collectionVariables.get('invStockCode')));"] }),
  req('Update sales draft (qty 4)', 'PUT', '/invoices/{{saleId}}', { auth: 'operatorToken', body: invoiceBody('Sales', '{{customerId}}', [line(4, 15, 20)], { rowVersion: '{{saleRowVersion}}' }), tests: [status(200), "const b = pm.response.json();", "pm.test('totals 60/12/72', () => { pm.expect(b.subTotal).to.eql(60); pm.expect(b.grandTotal).to.eql(72); });", save('saleRowVersion2', 'b.rowVersion')] }),
  req('Approve with stale rowVersion → 409', 'POST', '/invoices/{{saleId}}/approve', { auth: 'operatorToken', body: { rowVersion: '{{saleRowVersion}}' }, tests: problem('CONCURRENCY_CONFLICT', 409) }),
  req('Approve sale → SAT number', 'POST', '/invoices/{{saleId}}/approve', { auth: 'operatorToken', body: { rowVersion: '{{saleRowVersion2}}' }, tests: [status(200), "const b = pm.response.json();", "pm.test('Approved SATnnnnnn', () => { pm.expect(b.status).to.eql('Approved'); pm.expect(b.documentNo).to.match(/^SAT\\d{6}$/); });", save('saleRowVersion3', 'b.rowVersion')] }),
  req('Approve again → 409 INVOICE_NOT_DRAFT', 'POST', '/invoices/{{saleId}}/approve', { auth: 'operatorToken', body: { rowVersion: '{{saleRowVersion3}}' }, tests: problem('INVOICE_NOT_DRAFT', 409) }),
  req('PUT approved → 409', 'PUT', '/invoices/{{saleId}}', { auth: 'operatorToken', body: invoiceBody('Sales', '{{customerId}}', [line(1, 1, 0)], { rowVersion: '{{saleRowVersion3}}' }), tests: problem('INVOICE_NOT_DRAFT', 409) }),
  req('DELETE approved → 409', 'DELETE', '/invoices/{{saleId}}', { auth: 'operatorToken', tests: problem('INVOICE_NOT_DRAFT', 409) }),
  req('Cancel as operator → 403', 'POST', '/invoices/{{saleId}}/cancel', { auth: 'operatorToken', body: { rowVersion: '{{saleRowVersion3}}', reason: 'Deneme' }, tests: problem('FORBIDDEN', 403) }),
  req('Cancel as admin → Cancelled', 'POST', '/invoices/{{saleId}}/cancel', { company: '{{demoCompanyId}}', body: { rowVersion: '{{saleRowVersion3}}', reason: 'Postman iptal testi' }, tests: [status(200), "const b = pm.response.json();", "pm.test('Cancelled', () => { pm.expect(b.status).to.eql('Cancelled'); pm.expect(b.cancelReason).to.eql('Postman iptal testi'); });", save('saleRowVersion4', 'b.rowVersion')] }),
  req('Cancel again → 409 INVOICE_NOT_APPROVED', 'POST', '/invoices/{{saleId}}/cancel', { company: '{{demoCompanyId}}', body: { rowVersion: '{{saleRowVersion4}}', reason: 'Tekrar' }, tests: problem('INVOICE_NOT_APPROVED', 409) }),
  req('Stock balance restored = 10', 'GET', '/stocks/{{invStockId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('balance 10', () => pm.expect(pm.response.json().balance).to.eql(10));"] }),
  req('Create draft to delete', 'POST', '/invoices', { auth: 'operatorToken', body: invoiceBody('Sales', '{{customerId}}', [line(1, 1, 1)]), tests: [status(201), save('draftId', 'pm.response.json().id')] }),
  req('Delete draft → 204', 'DELETE', '/invoices/{{draftId}}', { auth: 'operatorToken', tests: status(204) }),
  req('Get deleted draft → 404', 'GET', '/invoices/{{draftId}}', { auth: 'operatorToken', tests: problem('NOT_FOUND', 404) }),
  req('List invoices (filters)', 'GET', '/invoices?currentAccountId={{customerId}}&type=Sales&status=Cancelled&sort=-invoiceDate,documentNo', { auth: 'operatorToken', tests: [status(200), ...paged(), "pm.test('one cancelled sale', () => pm.expect(pm.response.json().totalCount).to.eql(1));"] }),
  req('Get invoice detail', 'GET', '/invoices/{{saleId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('has lines', () => pm.expect(pm.response.json().lines).to.have.length(1));"] }),
]);

const transactions = folder('Transactions', 'Read-only ledgers and dashboard.', [
  req('Stock trans of cancelled sale (Out + reversal In)', 'GET', '/stock-trans?invoiceId={{saleId}}&sort=createdAt', { auth: 'operatorToken', tests: [status(200), ...paged(), "const it = pm.response.json().items;", "pm.test('2 rows: Out and reversal In', () => { pm.expect(it).to.have.length(2); pm.expect(it.filter(r => r.isReversal).map(r => r.direction)).to.eql(['In']); pm.expect(it.filter(r => !r.isReversal).map(r => r.direction)).to.eql(['Out']); });"] }),
  req('Act trans of cancelled sale (Debit + reversal Credit)', 'GET', '/act-trans?invoiceId={{saleId}}', { auth: 'operatorToken', tests: [status(200), "const it = pm.response.json().items;", "pm.test('Debit 72 + Credit 72', () => { pm.expect(it).to.have.length(2); it.forEach(r => pm.expect(r.amount).to.eql(72)); });"] }),
  req('Stock transactions sub-resource', 'GET', '/stocks/{{invStockId}}/transactions?direction=In', { auth: 'operatorToken', tests: [status(200), "pm.test('all In', () => pm.response.json().items.forEach(r => pm.expect(r.direction).to.eql('In')));"] }),
  req('Account transactions sub-resource', 'GET', '/current-accounts/{{customerId}}/transactions', { auth: 'operatorToken', tests: [status(200), ...paged()] }),
  req('Act trans unknown sort (quantity) → 400', 'GET', '/act-trans?sort=quantity', { auth: 'operatorToken', tests: problem('VALIDATION_FAILED', 400) }),
  req('Dashboard summary', 'GET', '/dashboard/summary', { auth: 'operatorToken', tests: [status(200), "const b = pm.response.json();", "pm.test('shape', () => pm.expect(b).to.include.all.keys('stockCount','currentAccountCount','draftInvoiceCount','approvedInvoiceCountThisMonth','salesTotalThisMonth','purchaseTotalThisMonth','totalReceivable','totalPayable','lowestStocks','recentInvoices'));", "pm.test('lowestStocks ≤ 5', () => pm.expect(b.lowestStocks.length).to.be.at.most(5));"] }),
]);

const portal = folder('Portal', 'Portal user (ahmet) sees only his own account, transactions and non-draft invoices.', [
  req('My account', 'GET', '/me/account', { auth: 'portalToken', tests: [status(200), "pm.test('C001', () => pm.expect(pm.response.json().code).to.eql('C001'));"] }),
  req('My transactions', 'GET', '/me/transactions', { auth: 'portalToken', tests: [status(200), ...paged()] }),
  req('My invoices (never Draft)', 'GET', '/me/invoices?pageSize=100', { auth: 'portalToken', tests: [status(200), "pm.test('no drafts, own account only', () => pm.response.json().items.forEach(i => { pm.expect(i.status).not.to.eql('Draft'); pm.expect(i.currentAccountCode).to.eql('C001'); }));"] }),
  req('Other customer invoice → 404', 'GET', '/me/invoices/{{saleId}}', { auth: 'portalToken', tests: problem('NOT_FOUND', 404) }),
  req('Portal → /stocks 403', 'GET', '/stocks', { auth: 'portalToken', tests: problem('FORBIDDEN', 403) }),
  req('Operator → /me/account 403', 'GET', '/me/account', { auth: 'operatorToken', tests: problem('FORBIDDEN', 403) }),
]);

const admin = folder('Admin', 'Reconciliation (AdminOnly) and health.', [
  req('Reconciliation run (no fix)', 'POST', '/admin/reconciliation/run?fix=false', { tests: [status(200), "const b = pm.response.json();", "pm.test('shape', () => pm.expect(b).to.include.all.keys('checkedAt','stocksChecked','accountsChecked','fixed','discrepancies'));", "pm.test('no discrepancies', () => pm.expect(b.discrepancies).to.have.length(0));"] }),
  req('Reconciliation as operator → 403', 'POST', '/admin/reconciliation/run', { auth: 'operatorToken', tests: problem('FORBIDDEN', 403) }),
  req('Health', 'GET', '/health', { host: '{{hostUrl}}', auth: 'none', tests: [status(200), "pm.test('sql healthy', () => pm.expect(pm.response.json().checks.sql.status).to.eql('Healthy'));"] }),
  req('Swagger JSON', 'GET', '/swagger/v1/swagger.json', { host: '{{hostUrl}}', auth: 'none', tests: [status(200), "pm.test('openapi doc with bearer scheme', () => { const b = pm.response.json(); pm.expect(b.paths).to.have.property('/api/invoices/{id}/approve'); pm.expect(JSON.stringify(b.components.securitySchemes).toLowerCase()).to.include('bearer'); });"] }),
]);

const scenario = folder('Scenario – Ahmet Elma', 'Reference scenario (CONTRACT §3.2): Ahmet buys 2 kg Elma × 50 @1% → 101.00; ledgers and balances verified, then cancelled (balances restored).', [
  req('1. Operator login', 'POST', '/auth/login', { auth: 'none', pre: [today], body: { email: '{{operatorEmail}}', password: '{{operatorPassword}}' }, tests: [status(200), save('operatorToken', 'pm.response.json().accessToken')] }),
  req('2. Read ELMA', 'GET', '/stocks?q=ELMA', { auth: 'operatorToken', tests: [status(200), "const s = pm.response.json().items.find(x => x.code === 'ELMA');", "pm.test('ELMA exists, Kg, VAT 1', () => { pm.expect(s).to.be.an('object'); pm.expect(s.unit).to.eql('Kg'); pm.expect(s.vatRate).to.eql(1); pm.expect(s.balance).to.be.at.least(2); });", save('elmaId', 's.id'), save('elmaBefore', 's.balance')] }),
  req('3. Read C001 Ahmet', 'GET', '/current-accounts?q=C001', { auth: 'operatorToken', tests: [status(200), "const a = pm.response.json().items.find(x => x.code === 'C001');", "pm.test('Ahmet Yılmaz', () => pm.expect(a.title).to.eql('Ahmet Yılmaz'));", save('ahmetId', 'a.id'), save('ahmetBefore', 'a.balance')] }),
  req('4. Create sales invoice (Draft)', 'POST', '/invoices', { auth: 'operatorToken', body: invoiceBody('Sales', '{{ahmetId}}', [line(2, 50, 1, '{{elmaId}}')], { description: 'Postman Ahmet/Elma' }), tests: [status(201), "const b = pm.response.json();", "pm.test('Draft 100 / 1 / 101, number null', () => { pm.expect(b.status).to.eql('Draft'); pm.expect(b.subTotal).to.eql(100); pm.expect(b.vatTotal).to.eql(1); pm.expect(b.grandTotal).to.eql(101); pm.expect(b.number).to.be.null; });", save('ahmetInvoiceId', 'b.id'), save('ahmetInvoiceRv', 'b.rowVersion')] }),
  req('5. Approve', 'POST', '/invoices/{{ahmetInvoiceId}}/approve', { auth: 'operatorToken', body: { rowVersion: '{{ahmetInvoiceRv}}' }, tests: [status(200), "const b = pm.response.json();", "pm.test('Approved SATnnnnnn', () => { pm.expect(b.status).to.eql('Approved'); pm.expect(b.documentNo).to.match(/^SAT\\d{6}$/); });", save('ahmetInvoiceRv2', 'b.rowVersion'), save('ahmetDocNo', 'b.documentNo')] }),
  req('6. StockTrans: one Out 2 ELMA', 'GET', '/stock-trans?invoiceId={{ahmetInvoiceId}}', { auth: 'operatorToken', tests: [status(200), "const it = pm.response.json().items;", "pm.test('exactly one Out 2.000 row for ELMA', () => { pm.expect(it).to.have.length(1); pm.expect(it[0].stockCode).to.eql('ELMA'); pm.expect(it[0].direction).to.eql('Out'); pm.expect(it[0].quantity).to.eql(2); pm.expect(it[0].isReversal).to.be.false; pm.expect(it[0].documentNo).to.eql(pm.collectionVariables.get('ahmetDocNo')); });"] }),
  req('7. ActTrans: one Debit 101 Ahmet', 'GET', '/act-trans?invoiceId={{ahmetInvoiceId}}', { auth: 'operatorToken', tests: [status(200), "const it = pm.response.json().items;", "pm.test('exactly one Debit 101.00 row for C001', () => { pm.expect(it).to.have.length(1); pm.expect(it[0].currentAccountCode).to.eql('C001'); pm.expect(it[0].direction).to.eql('Debit'); pm.expect(it[0].amount).to.eql(101); });"] }),
  req('8. ELMA balance −2', 'GET', '/stocks/{{elmaId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('balance = before − 2', () => pm.expect(pm.response.json().balance).to.eql(Number(pm.collectionVariables.get('elmaBefore')) - 2));"] }),
  req('9. Ahmet balance +101', 'GET', '/current-accounts/{{ahmetId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('balance = before + 101', () => pm.expect(pm.response.json().balance).to.be.closeTo(Number(pm.collectionVariables.get('ahmetBefore')) + 101, 0.001));"] }),
  req('10. Portal: Ahmet sees the balance and invoice', 'GET', '/me/invoices/{{ahmetInvoiceId}}', { auth: 'portalToken', tests: [status(200), "pm.test('own approved invoice', () => pm.expect(pm.response.json().documentNo).to.eql(pm.collectionVariables.get('ahmetDocNo')));"] }),
  req('11. Admin cancels', 'POST', '/invoices/{{ahmetInvoiceId}}/cancel', { company: '{{demoCompanyId}}', body: { rowVersion: '{{ahmetInvoiceRv2}}', reason: 'Postman senaryo iptali' }, tests: [status(200), "pm.test('Cancelled', () => pm.expect(pm.response.json().status).to.eql('Cancelled'));"] }),
  req('12. ELMA balance restored', 'GET', '/stocks/{{elmaId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('balance = before', () => pm.expect(pm.response.json().balance).to.eql(Number(pm.collectionVariables.get('elmaBefore'))));"] }),
  req('13. Ahmet balance restored', 'GET', '/current-accounts/{{ahmetId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('balance = before', () => pm.expect(pm.response.json().balance).to.be.closeTo(Number(pm.collectionVariables.get('ahmetBefore')), 0.001));"] }),
  req('14. Reversal rows present', 'GET', '/stock-trans?invoiceId={{ahmetInvoiceId}}', { auth: 'operatorToken', tests: [status(200), "pm.test('Out + reversal In', () => { const it = pm.response.json().items; pm.expect(it).to.have.length(2); pm.expect(it.some(r => r.isReversal && r.direction === 'In')).to.be.true; });"] }),
]);

const knownBugs = folder('Known bugs', 'Contract assertions for open bugs (docs/TEST_REPORT.md). Skipped unless the environment variable runKnownBugs = true.', [
  req('BUG-1 Operator GET /companies/current → 200', 'GET', '/companies/current', { auth: 'operatorToken', tests: [
    "const run = pm.environment.get('runKnownBugs') === 'true';",
    "(run ? pm.test : pm.test.skip)('BUG-1: operator gets own company (contract §5.5)', () => { pm.response.to.have.status(200); pm.expect(pm.response.json().id).to.eql(Number(pm.collectionVariables.get('demoCompanyId'))); });",
  ] }),
]);

const collection = {
  info: {
    _postman_id: randomUUID(),
    name: 'StockAccount API',
    description: 'StockAccountApp API (CONTRACT v1.1). Folders run in order; tokens and ids are captured into collection variables. The refresh token is an httpOnly cookie kept by the cookie jar. Generated by build-collection.mjs.',
    schema: 'https://schema.getpostman.com/json/collection/v2.1.0/collection.json',
  },
  auth: { type: 'bearer', bearer: [{ key: 'token', value: '{{accessToken}}', type: 'string' }] },
  event: [
    { listen: 'prerequest', script: { type: 'text/javascript', exec: collectionPre } },
    { listen: 'test', script: { type: 'text/javascript', exec: collectionTests } },
  ],
  variable: ['accessToken', 'accessTokenExpiresAt', 'adminToken', 'operatorToken', 'portalToken', 'demoCompanyId', 'today'].map((key) => ({ key, value: '' })),
  item: [auth, companies, users, stocks, accounts, invoices, transactions, portal, admin, scenario, knownBugs],
};

writeFileSync(new URL('./StockAccount.postman_collection.json', import.meta.url), JSON.stringify(collection, null, 2) + '\n');
const count = collection.item.reduce((n, f) => n + f.item.length, 0);
console.log(`wrote StockAccount.postman_collection.json: ${collection.item.length} folders, ${count} requests`);
