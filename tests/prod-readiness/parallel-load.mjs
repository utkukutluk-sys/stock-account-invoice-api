// Fires N parallel authenticated DB-bound requests (half admin, half operator) and reports status codes and latency.
//   node parallel-load.mjs http://localhost:5080 300
import { resolveTarget, STAGING_URL, stagingAdmin } from './_guard.mjs';
// Default target: STAGING (bootstrap admin plays both users). The dev stack needs --i-know-this-is-dev.
const { args, staging } = resolveTarget(process.argv.slice(2));
const sa = staging ? stagingAdmin() : {};
const [base = STAGING_URL, n = '300'] = args;
async function login(email, password) {
  const r = await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
  if (r.status !== 200) throw new Error(`login ${email} → ${r.status}`);
  return (await r.json()).accessToken;
}
const tokens = [await login(sa.email ?? 'admin@stockaccount.local', sa.password ?? 'Admin123!'), await login(sa.email ?? 'demo@stockaccount.local', sa.password ?? 'Demo123!')];
const urls = ['/api/dashboard/summary', '/api/invoices?pageSize=100', '/api/stock-trans?pageSize=100', '/api/act-trans?pageSize=100'];
const t0 = Date.now();
const results = await Promise.all(Array.from({ length: Number(n) }, async (_, i) => {
  const s = Date.now();
  try {
    const r = await fetch(base + urls[i % urls.length], { headers: { Authorization: `Bearer ${tokens[i % 2]}` } });
    await r.arrayBuffer();
    return { status: r.status, ms: Date.now() - s };
  } catch (e) { return { status: 'ERR', ms: Date.now() - s }; }
}));
const hist = {};
for (const r of results) hist[r.status] = (hist[r.status] ?? 0) + 1;
const ms = results.map((r) => r.ms).sort((a, b) => a - b);
console.log(`requests=${n} total=${Date.now() - t0} ms statuses=${JSON.stringify(hist)} p50=${ms[Math.floor(ms.length * 0.5)]} ms p95=${ms[Math.floor(ms.length * 0.95)]} ms max=${ms.at(-1)} ms`);
