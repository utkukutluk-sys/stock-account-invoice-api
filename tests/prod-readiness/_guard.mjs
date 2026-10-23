// Shared target guard for tests/prod-readiness/*.mjs.
// DEFAULT TARGET = STAGING (https://localhost:8443, self-signed local CA => TLS verification is relaxed for that host only).
// The default dev stack (project "stockaccount": ports 5080 API, 5173 ui-vue, 3000 ui-nuxt) is REFUSED unless --i-know-this-is-dev
// is passed. Positional arguments are returned without the flag.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

export const STAGING_URL = 'https://localhost:8443';
const DEV_PORTS = new Set(['5080', '5173', '3000']);

export function isDevUrl(u) {
  try {
    const url = new URL(u);
    return ['localhost', '127.0.0.1', '[::1]', '::1'].includes(url.hostname) && DEV_PORTS.has(url.port);
  } catch { return false; }
}

/** Parses argv (without node/script). Returns { args, devOk, staging }. Exits 2 when a dev URL is used without the flag. */
export function resolveTarget(argv, urlArgIndexes = [0], defaultStaging = true) {
  const devOk = argv.includes('--i-know-this-is-dev');
  const args = argv.filter((a) => a !== '--i-know-this-is-dev');
  for (const i of urlArgIndexes) {
    const u = args[i];
    if (u && isDevUrl(u) && !devOk) {
      console.error(`REFUSED: ${u} is the default dev stack (stockaccount, ports 5080/5173/3000). The default target is staging (${STAGING_URL}); pass --i-know-this-is-dev to run against dev.`);
      process.exit(2);
    }
  }
  const usesStaging = urlArgIndexes.some((i) => (!args[i] && defaultStaging) || (args[i] ?? '').startsWith('https://localhost'));
  if (usesStaging) process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'; // Caddy local CA of the staging stack
  return { args, devOk, staging: usesStaging };
}

/** Bootstrap admin of the staging stack (.env.staging), used when no credentials are given. */
export function stagingAdmin() {
  try {
    const f = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '.env.staging');
    const t = readFileSync(f, 'utf8');
    const get = (k) => (t.match(new RegExp(`^${k}=(.*)$`, 'm')) ?? [])[1]?.trim();
    return { email: get('BOOTSTRAP_ADMIN_EMAIL'), password: get('BOOTSTRAP_ADMIN_PASSWORD') };
  } catch { return {}; }
}
