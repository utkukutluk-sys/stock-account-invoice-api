// Round 3 item 3: error-handling fuzz against an API running in Production mode.
// Every response (status line, headers, body) is scanned for information leaks: stack traces, exception type names,
// file paths, SQL text, framework/server versions.
//   node fuzz-errors.mjs [https://localhost:8443 [opEmail opPw adminEmail adminPw]]   (default: staging)
//   node fuzz-errors.mjs http://localhost:5090 demo@stockaccount.local Demo123! admin@stockaccount.local Admin123!
import { resolveTarget, STAGING_URL, stagingAdmin } from './_guard.mjs';
// Default target: STAGING (the bootstrap admin of .env.staging plays both roles). The dev stack needs --i-know-this-is-dev.
const { args, staging } = resolveTarget(process.argv.slice(2));
const sa = staging ? stagingAdmin() : {};
const [base = STAGING_URL, opEmail = sa.email ?? 'demo@stockaccount.local', opPw = sa.password ?? 'Demo123!', adEmail = sa.email ?? 'admin@stockaccount.local', adPw = sa.password ?? 'Admin123!'] = args;

const LEAK_PATTERNS = [
  [/\bat [A-Z][\w.]+\.[\w<>]+\(/, 'stack frame'],
  [/[A-Z]\w*Exception\b/, 'exception type'],
  [/\bSystem\.[A-Z]\w+/, 'System.* type'],
  [/\bMicrosoft\.[A-Z]\w+/, 'Microsoft.* type'],
  [/\bStockAccount\.[A-Z]\w+/, 'app namespace'],
  [/\.cs(:line| line)?\b/, '.cs file'],
  [/[A-Za-z]:\\|\/app\/|\/src\//, 'file path'],
  [/\b(SELECT|INSERT INTO|UPDATE \[|DELETE FROM|OFFSET \d|FETCH NEXT)\b/, 'SQL text'],
  [/SqlException|Microsoft\.Data\.SqlClient|Msg \d{3,5}/, 'SQL error'],
  [/Kestrel|ASP\.NET|aspnetcore|\.NET \d/i, 'framework name'],
  [/Npgsql|EntityFramework|EFCore/i, 'ORM'],
  [/could not be converted|is required\.|The JSON value|Path: \$/, 'framework English message'],
  [/"exception"\s*:/, 'exception extension'],
];
const BAD_HEADERS = ['server', 'x-powered-by', 'x-aspnet-version', 'x-aspnetmvc-version', 'x-sourcefiles'];

async function login(email, password) {
  const r = await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
  if (r.status !== 200) throw new Error(`login ${email}: ${r.status} ${await r.text()}`);
  return (await r.json()).accessToken;
}

const op = await login(opEmail, opPw);
const admin = await login(adEmail, adPw);
const J = 'application/json';
const big = (n) => 'x'.repeat(n);
const deep = (n) => '['.repeat(n) + ']'.repeat(n);

/** [name, method, path, { body, type, token, headers }] */
const cases = [
  ['malformed JSON stock', 'POST', '/api/stocks', { body: '{"code":', type: J }],
  ['malformed JSON invoice', 'POST', '/api/invoices', { body: '{"type":"Sales","lines":[{]}', type: J }],
  ['malformed JSON login', 'POST', '/api/auth/login', { body: '{email:x}', type: J, token: null }],
  ['wrong JSON types', 'POST', '/api/stocks', { body: JSON.stringify({ code: 123, name: { a: 1 }, unit: [1], vatRate: 'x', unitPrice: true }), type: J }],
  ['unknown enum', 'POST', '/api/stocks', { body: JSON.stringify({ code: 'FZ1', name: 'x', unit: 'Ton', vatRate: 1, unitPrice: 1, isActive: true }), type: J }],
  ['JSON depth 1000', 'POST', '/api/invoices', { body: `{"lines":${deep(1000)}}`, type: J }],
  ['decimal overflow in JSON', 'POST', '/api/stocks', { body: '{"code":"FZ2","name":"x","unit":"Kg","vatRate":1,"unitPrice":1e40,"isActive":true}', type: J }],
  ['quantity exceeds decimal(18,3)', 'POST', '/api/invoices', { body: JSON.stringify({ type: 'Sales', invoiceDate: '2026-10-22', currentAccountId: 1, lines: [{ stockId: 1, quantity: 999999999999999999.5, unitPrice: 1, vatRate: 1 }] }), type: J }],
  ['unit price exceeds decimal(18,4)', 'POST', '/api/invoices', { body: JSON.stringify({ type: 'Sales', invoiceDate: '2026-10-22', currentAccountId: 1, lines: [{ stockId: 1, quantity: 1, unitPrice: 99999999999999999, vatRate: 1 }] }), type: J }],
  ['5 MB description', 'POST', '/api/stocks', { body: JSON.stringify({ code: 'FZ3', name: 'x', unit: 'Kg', vatRate: 1, unitPrice: 1, isActive: true, description: big(5_000_000) }), type: J }],
  ['40 MB body (> Kestrel limit)', 'POST', '/api/stocks', { body: JSON.stringify({ description: big(40_000_000) }), type: J }],
  ['text/plain body', 'POST', '/api/stocks', { body: 'code=1', type: 'text/plain' }],
  ['application/xml body', 'POST', '/api/stocks', { body: '<stock/>', type: 'application/xml' }],
  ['form body', 'POST', '/api/stocks', { body: 'code=1&name=2', type: 'application/x-www-form-urlencoded' }],
  ['no content type', 'POST', '/api/stocks', { body: '{}', type: null }],
  ['refresh with text/plain', 'POST', '/api/auth/refresh', { body: '{}', type: 'text/plain', token: null }],
  ['route id not int', 'GET', '/api/stocks/abc', {}],
  ['route id overflow', 'GET', '/api/stocks/99999999999', {}],
  ['route id negative', 'GET', '/api/stocks/-1', {}],
  ['route id decimal', 'GET', '/api/invoices/1.5', {}],
  ['encoded NUL in route', 'GET', '/api/stocks/%00', {}],
  ['path traversal', 'GET', '/api/stocks/..%2F..%2Fappsettings.json', {}],
  ['query page=abc', 'GET', '/api/stocks?page=abc', {}],
  ['query pageSize huge', 'GET', '/api/stocks?pageSize=99999999999', {}],
  ['query page=int.Max', 'GET', '/api/stocks?page=2147483647&pageSize=100', {}],
  ['query page=int.Max invoices', 'GET', '/api/invoices?page=2147483647&pageSize=100', {}],
  ['query page=int.Max stock-trans', 'GET', '/api/stock-trans?page=2147483647&pageSize=100', {}],
  ['query page=int.Max users (admin)', 'GET', '/api/users?page=2147483647&pageSize=100', { token: 'admin' }],
  ['query sort injection', 'GET', "/api/stocks?sort=code;DROP TABLE Stocks--", {}],
  ['query dateFrom invalid', 'GET', '/api/invoices?dateFrom=not-a-date', {}],
  ['query dateFrom > dateTo', 'GET', '/api/invoices?dateFrom=2026-12-01&dateTo=2026-01-01', {}],
  ['query enum invalid', 'GET', '/api/stock-trans?direction=Sideways', {}],
  ['query q 10 KB', 'GET', `/api/stocks?q=${big(10_000)}`, {}],
  ['URL 100 KB', 'GET', `/api/stocks?q=${big(100_000)}`, {}],
  ['header 64 KB', 'GET', '/api/stocks', { headers: { 'X-Big': big(64_000) } }],
  ['X-Company-Id abc (admin)', 'GET', '/api/stocks', { token: 'admin', headers: { 'X-Company-Id': 'abc' } }],
  ['X-Company-Id overflow (admin)', 'GET', '/api/stocks', { token: 'admin', headers: { 'X-Company-Id': '99999999999' } }],
  ['bearer garbage', 'GET', '/api/stocks', { token: 'garbage.token.value' }],
  ['bearer alg none', 'GET', '/api/stocks', { token: 'eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiIxIiwicm9sZSI6IkFkbWluIn0.' }],
  ['rowVersion not base64', 'PUT', '/api/stocks/1', { body: JSON.stringify({ code: 'ELMA', name: 'Elma', unit: 'Kg', vatRate: 1, unitPrice: 50, isActive: true, rowVersion: '%%%' }), type: J }],
  ['approve rowVersion wrong length', 'POST', '/api/invoices/1/approve', { body: JSON.stringify({ rowVersion: 'AAAA' }), type: J }],
  ['PATCH (405)', 'PATCH', '/api/stocks/1', { body: '{}', type: J }],
  ['PUT on collection (405)', 'PUT', '/api/stocks', { body: '{}', type: J }],
  ['DELETE on collection (405)', 'DELETE', '/api/invoices', {}],
  ['TRACE', 'TRACE', '/api/stocks', {}],
  ['OPTIONS', 'OPTIONS', '/api/stocks', {}],
  ['unknown api route', 'GET', '/api/does-not-exist', {}],
  ['unknown root route', 'GET', '/nope', { token: null }],
  ['swagger UI (disabled in Production)', 'GET', '/swagger/index.html', { token: null }],
  ['swagger json (disabled in Production)', 'GET', '/swagger/v1/swagger.json', { token: null }],
  ['appsettings.json probe', 'GET', '/appsettings.json', { token: null }],
  ['.env probe', 'GET', '/.env', { token: null }],
  ['health ready', 'GET', '/health/ready', { token: null }],
  ['anonymous protected', 'GET', '/api/invoices', { token: null }],
  ['portal endpoint as operator (403)', 'GET', '/api/me/account', {}],
];

const results = [];
for (const [name, method, path, o] of cases) {
  const headers = { ...(o.headers ?? {}) };
  if (o.type) headers['Content-Type'] = o.type;
  const token = o.token === undefined ? op : o.token === 'admin' ? admin : o.token;
  if (token) headers.Authorization = `Bearer ${token}`;
  let status = 'ERR', text = '', hdrs = {};
  try {
    const r = await fetch(base + path, { method, headers, body: o.body });
    status = r.status;
    hdrs = Object.fromEntries(r.headers.entries());
    text = await r.text();
  } catch (e) {
    text = `fetch error: ${e.message}`;
  }
  const blob = JSON.stringify(hdrs) + '\n' + text;
  const leaks = LEAK_PATTERNS.filter(([re]) => re.test(blob)).map(([, label]) => label);
  for (const h of BAD_HEADERS) if (hdrs[h]) leaks.push(`header ${h}: ${hdrs[h]}`);
  const ct = (hdrs['content-type'] ?? '').split(';')[0];
  const problem = ct === 'application/problem+json';
  let code = '';
  try { code = JSON.parse(text).code ?? ''; } catch { /* not JSON */ }
  const errStatus = typeof status === 'number' && status >= 400 && status !== 404 ? true : typeof status === 'number' && status >= 400;
  results.push({ name, method, path: path.length > 60 ? path.slice(0, 57) + '...' : path, status, ct, code, problemJson: errStatus ? problem : '-', leaks, sample: leaks.length ? text.slice(0, 300) : '' });
}

let leakCount = 0, nonProblem = 0, fiveHundreds = 0;
for (const r of results) {
  if (r.leaks.length) leakCount++;
  if (typeof r.status === 'number' && r.status >= 400 && !r.problemJson) nonProblem++;
  if (r.status >= 500) fiveHundreds++;
  console.log(`${String(r.status).padEnd(4)}| ${r.name.padEnd(38)}| ${r.method.padEnd(7)}| ${(r.ct || '-').padEnd(26)}| ${String(r.code).padEnd(20)}| ${r.leaks.length ? 'LEAK: ' + r.leaks.join(', ') : 'clean'}`);
  if (r.sample) console.log(`      sample: ${r.sample.replace(/\s+/g, ' ')}`);
}
console.log(`\nSUMMARY cases=${results.length} leaks=${leakCount} errorResponsesNotProblemJson=${nonProblem} status5xx=${fiveHundreds}`);
