// Round 3 item 1a: prove that EVERY operation in swagger.json is rate limited.
// Needs an API in Development (Swagger on) with tiny limits, e.g. (from e2e/):
//   E2E_API_PORT=5091 E2E_DB=StockAccountDb_E2E_RL RateLimiting__Global__PermitLimit=5 RateLimiting__Global__WindowSeconds=3 \
//   RateLimiting__Auth__PermitLimit=2 RateLimiting__Auth__WindowSeconds=3 node scripts/start-api.mjs
//   node rate-limit-coverage.mjs http://localhost:5091 5 2 3
// The API is reached from loopback, which is a trusted proxy by default, so X-Forwarded-For selects the client IP
// (used to give anonymous calls separate IP buckets).
const [base = 'http://localhost:5091', globalLimit = '5', authLimit = '2', windowSec = '3'] = process.argv.slice(2);
const G = Number(globalLimit), A = Number(authLimit), W = Number(windowSec) * 1000 + 700;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let ipSeq = 1;
const freshIp = () => `10.77.${Math.floor(ipSeq / 250)}.${(ipSeq++ % 250) + 1}`;

async function login(email, password) {
  const r = await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Forwarded-For': freshIp() }, body: JSON.stringify({ email, password }) });
  if (r.status !== 200) throw new Error(`login ${email} → ${r.status} ${await r.text()}`);
  return (await r.json()).accessToken;
}

const swagger = await (await fetch(`${base}/swagger/v1/swagger.json`)).json();
const ops = [];
for (const [path, item] of Object.entries(swagger.paths)) for (const method of Object.keys(item)) ops.push({ method: method.toUpperCase(), path, op: item[method] });
const admin = await login('admin@stockaccount.local', 'Admin123!');
const portal = await login('ahmet@stockaccount.local', 'Ahmet123!');
const operator = await login('demo@stockaccount.local', 'Demo123!');
const authPolicy = new Set(['POST /api/auth/login', 'POST /api/auth/refresh']);
const anonymous = new Set(['POST /api/auth/login', 'POST /api/auth/refresh', 'POST /api/auth/logout']);

async function call(method, path, { token, ip } = {}) {
  const headers = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  if (ip) headers['X-Forwarded-For'] = ip;
  let body;
  if (['POST', 'PUT', 'PATCH'].includes(method)) { headers['Content-Type'] = 'application/json'; body = '{}'; }
  const r = await fetch(base + path, { method, headers, body });
  const text = await r.text();
  return { status: r.status, retryAfter: r.headers.get('retry-after'), ct: (r.headers.get('content-type') ?? '').split(';')[0], code: (() => { try { return JSON.parse(text).code; } catch { return ''; } })() };
}

const rows = [];
let covered = 0;
for (const { method, path } of ops) {
  const key = `${method} ${path}`;
  const url = path.replace(/\{[^}]+\}/g, '999999');
  const isAuth = authPolicy.has(key);
  const anon = anonymous.has(key);
  const limit = isAuth ? A : G;
  const ip = anon ? freshIp() : undefined; // anonymous → its own IP bucket; authenticated → user bucket (waited out)
  const token = anon ? undefined : path.startsWith('/api/me') ? portal : admin;
  const statuses = [];
  let last;
  for (let i = 0; i <= limit; i++) { last = await call(method, url, { token, ip }); statuses.push(last.status); }
  const firstOk = statuses.slice(0, limit).every((s) => s !== 429);
  const limited = last.status === 429 && last.code === 'RATE_LIMITED' && last.ct === 'application/problem+json' && !!last.retryAfter;
  if (firstOk && limited) covered++;
  rows.push(`${firstOk && limited ? 'PASS' : 'FAIL'} | ${key.padEnd(48)} | ${isAuth ? 'auth  ' : 'global'} limit ${limit} | partition ${anon ? 'ip  ' : 'user'} | ${statuses.join(',')} | Retry-After=${last.retryAfter ?? '-'} ${last.ct}`);
  if (!anon) await sleep(W); // let the user's window expire before the next operation
}
console.log(rows.join('\n'));
console.log(`\nOPERATIONS covered ${covered}/${ops.length}`);

// --- extra properties
const extra = [];
// per-user partition: admin exhausted, operator from the same IP still served
for (let i = 0; i <= G; i++) await call('GET', '/api/stocks', { token: admin });
const adminBlocked = (await call('GET', '/api/stocks', { token: admin })).status;
const opServed = (await call('GET', '/api/stocks', { token: operator })).status;
extra.push(`${adminBlocked === 429 && opServed === 200 ? 'PASS' : 'FAIL'} | per-user partition: admin after ${G + 1} req → ${adminBlocked}, operator (same IP) → ${opServed}`);
// per-IP partition for anonymous callers
const ipA = freshIp(), ipB = freshIp();
const seqA = []; for (let i = 0; i <= G; i++) seqA.push((await call('GET', '/api/stocks', { ip: ipA })).status);
const bFirst = (await call('GET', '/api/stocks', { ip: ipB })).status;
extra.push(`${seqA.at(-1) === 429 && bFirst === 401 ? 'PASS' : 'FAIL'} | per-IP partition (anonymous): ip A ${seqA.join(',')} ; ip B first → ${bFirst}`);
// unknown routes are limited too
const unk = []; const ipC = freshIp(); for (let i = 0; i <= G; i++) unk.push((await call('GET', '/api/no-such-route', { ip: ipC })).status);
extra.push(`${unk.at(-1) === 429 ? 'PASS' : 'FAIL'} | unknown /api route limited: ${unk.join(',')}`);
// auth policy is stricter than global and per IP (independent of the global bucket)
const ipD = freshIp(); const lg = [];
for (let i = 0; i <= A; i++) lg.push((await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Forwarded-For': ipD }, body: JSON.stringify({ email: 'nobody@x.local', password: 'Wrong12345' }) })).status);
const otherAnonSameIp = (await call('GET', '/api/stocks', { ip: ipD })).status;
extra.push(`${lg.at(-1) === 429 && otherAnonSameIp === 401 ? 'PASS' : 'FAIL'} | auth policy (limit ${A} < global ${G}): login ${lg.join(',')} ; same IP GET /api/stocks → ${otherAnonSameIp}`);
// health endpoints excluded
for (const h of ['/health', '/health/live', '/health/ready']) {
  const s = []; const ipH = freshIp(); for (let i = 0; i < G * 6; i++) s.push((await call('GET', h, { ip: ipH })).status);
  extra.push(`${s.every((x) => x !== 429) ? 'PASS' : 'FAIL'} | ${h} excluded: ${G * 6} requests → ${[...new Set(s)].join('/')}`);
}
// Retry-After honesty: after waiting Retry-After the caller is served again
await sleep(W);
const ipE = freshIp(); let r; for (let i = 0; i <= G; i++) r = await call('GET', '/api/stocks', { ip: ipE });
const wait = Number(r.retryAfter);
await sleep(wait * 1000 + 300);
const after = (await call('GET', '/api/stocks', { ip: ipE })).status;
extra.push(`${r.status === 429 && after === 401 ? 'PASS' : 'FAIL'} | Retry-After=${wait}s honoured: after waiting → ${after}`);
console.log(extra.join('\n'));
