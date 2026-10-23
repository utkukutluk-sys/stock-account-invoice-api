// Round 3 item 3 (UIs): error pages of ui-vue (nginx) and ui-nuxt (Nitro SSR) must not leak stack traces, paths or versions.
//   node fuzz-ui-errors.mjs http://localhost:5173 http://localhost:3000
import { resolveTarget, STAGING_URL, stagingAdmin } from './_guard.mjs';
// Default target: STAGING (only the Vue UI behind Caddy runs there; ui-nuxt is skipped). The dev stack needs --i-know-this-is-dev.
const { args, staging } = resolveTarget(process.argv.slice(2), [0, 1]);
const sa = staging ? stagingAdmin() : {};
const [vue = STAGING_URL, nuxt = staging ? '' : 'http://localhost:3000'] = args;
const LEAKS = [
  [/\bat [\w$.<>]+ \(|\n\s+at /, 'stack frame'],
  [/[A-Z]\w*(Exception|Error):/, 'error type with message'],
  [/node_modules|\/app\/|[A-Za-z]:\\|\.output\/server|\.mjs:\d+|\.ts:\d+/, 'file path'],
  [/nginx\/\d|Node\.js|nitro\/\d|nuxt\/\d|vue@\d|express/i, 'server/framework version'],
  [/SELECT |SqlException|Microsoft\.|System\./, 'backend internals'],
  [/"stack"\s*:\s*"[^"]{5,}/, 'stack in payload'],
];
const BAD_HEADERS = ['x-powered-by', 'x-aspnet-version', 'x-nitro-prerender'];

async function login(base) {
  const r = await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: sa.email ?? 'demo@stockaccount.local', password: sa.password ?? 'Demo123!' }) });
  const cookie = (r.headers.getSetCookie?.() ?? []).map((c) => c.split(';')[0]).join('; ');
  return { token: (await r.json()).accessToken, cookie };
}

const out = [];
async function probe(ui, base, name, method, path, { headers = {}, body } = {}) {
  let status = 'ERR', text = '', hdrs = {};
  try {
    const r = await fetch(base + path, { method, headers, body, redirect: 'manual' });
    status = r.status; hdrs = Object.fromEntries(r.headers.entries()); text = await r.text();
  } catch (e) { text = `fetch error ${e.message}`; }
  const blob = JSON.stringify(hdrs) + '\n' + text;
  const leaks = LEAKS.filter(([re]) => re.test(blob)).map(([, l]) => l);
  for (const h of BAD_HEADERS) if (hdrs[h]) leaks.push(`header ${h}=${hdrs[h]}`);
  const server = hdrs.server ?? '-';
  if (/\d/.test(server)) leaks.push(`Server header with version: ${server}`);
  out.push({ ui, name, method, path: path.slice(0, 50), status, ct: (hdrs['content-type'] ?? '-').split(';')[0], server, csp: hdrs['content-security-policy'] ? 'yes' : 'no', leaks, sample: leaks.length ? text.slice(0, 400) : '' });
}

for (const [ui, base] of [['vue', vue], ['nuxt', nuxt]].filter(([, b]) => b)) {
  const { token, cookie } = await login(base);
  const auth = { Authorization: `Bearer ${token}` };
  await probe(ui, base, 'unknown page route', 'GET', '/this/does/not/exist');
  await probe(ui, base, 'unknown page route (with session)', 'GET', '/this/does/not/exist', { headers: { Cookie: cookie } });
  await probe(ui, base, 'page with non-numeric id', 'GET', '/invoices/abc', { headers: { Cookie: cookie } });
  await probe(ui, base, 'page for missing record', 'GET', '/stocks/99999999', { headers: { Cookie: cookie } });
  await probe(ui, base, 'page whose API call returns 500', 'GET', '/stocks?page=2147483647&pageSize=100', { headers: { Cookie: cookie } });
  await probe(ui, base, 'page with invalid query', 'GET', '/invoices?dateFrom=nope&pageSize=9999', { headers: { Cookie: cookie } });
  await probe(ui, base, 'encoded garbage path', 'GET', '/%E0%A4%A');
  await probe(ui, base, 'path traversal', 'GET', '/..%2f..%2fetc%2fpasswd');
  await probe(ui, base, 'missing static asset', 'GET', '/assets/missing-chunk-123.js');
  await probe(ui, base, 'missing _nuxt asset', 'GET', '/_nuxt/missing-chunk-123.js');
  await probe(ui, base, 'URL 100 KB', 'GET', '/stocks?q=' + 'x'.repeat(100_000));
  await probe(ui, base, 'header 64 KB', 'GET', '/login', { headers: { 'X-Big': 'x'.repeat(64_000) } });
  await probe(ui, base, 'proxy: API 500 via /api', 'GET', '/api/stocks?page=2147483647&pageSize=100', { headers: auth });
  await probe(ui, base, 'proxy: unknown /api route', 'GET', '/api/nope', { headers: auth });
  await probe(ui, base, 'proxy: malformed JSON', 'POST', '/api/stocks', { headers: { ...auth, 'Content-Type': 'application/json' }, body: '{"code":' });
  await probe(ui, base, 'proxy: 5 MB body', 'POST', '/api/stocks', { headers: { ...auth, 'Content-Type': 'application/json' }, body: JSON.stringify({ description: 'x'.repeat(5_000_000) }) });
  await probe(ui, base, 'proxy: 40 MB body', 'POST', '/api/stocks', { headers: { ...auth, 'Content-Type': 'application/json' }, body: JSON.stringify({ description: 'x'.repeat(40_000_000) }) });
  await probe(ui, base, 'POST to a page route', 'POST', '/stocks', { headers: { 'Content-Type': 'application/json' }, body: '{}' });
  await probe(ui, base, 'source map probe', 'GET', '/assets/index.js.map');
  await probe(ui, base, '.env probe', 'GET', '/.env');
  await probe(ui, base, 'nuxt payload of 404', 'GET', '/this/does/not/exist/_payload.json');
}

for (const r of out) {
  console.log(`${r.ui.padEnd(4)} | ${String(r.status).padEnd(4)} | ${r.name.padEnd(34)} | ${r.ct.padEnd(24)} | server=${r.server.padEnd(6)} | csp=${r.csp} | ${r.leaks.length ? 'LEAK: ' + r.leaks.join(', ') : 'clean'}`);
  if (r.sample) console.log(`       sample: ${r.sample.replace(/\s+/g, ' ')}`);
}
console.log(`\nSUMMARY probes=${out.length} leaks=${out.filter((r) => r.leaks.length).length} 5xx=${out.filter((r) => r.status >= 500).length}`);
