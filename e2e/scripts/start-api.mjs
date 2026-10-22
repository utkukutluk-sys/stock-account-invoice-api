// Starts a dedicated API for E2E on a FRESH database (dropped first), so every run starts from the seed data.
//   E2E_API_PORT (default 5081), E2E_DB (default StockAccountDb_E2E), E2E_SQL_SERVER (default .\SQLEXPRESS)
// The API uses the in-memory cache (Redis keys of the dev DB would otherwise collide: same ids, other data).
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const port = process.env.E2E_API_PORT ?? '5081';
const db = process.env.E2E_DB ?? 'StockAccountDb_E2E';
const server = process.env.E2E_SQL_SERVER ?? '.\\SQLEXPRESS';
if (!/^StockAccountDb_E2E\w*$/.test(db)) throw new Error(`refusing to drop non-E2E database ${db}`);

if (process.env.E2E_KEEP_DB !== '1') {
  const drop = `IF DB_ID(N'${db}') IS NOT NULL BEGIN ALTER DATABASE [${db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [${db}]; END`;
  const r = spawnSync('sqlcmd', ['-S', server, '-E', '-b', '-Q', drop], { stdio: 'inherit' });
  if (r.status !== 0) throw new Error('could not drop E2E database (is sqlcmd on PATH?)');
  console.log(`[e2e] dropped ${db}`);
}

const env = {
  ...process.env,
  ASPNETCORE_ENVIRONMENT: 'Development',
  ASPNETCORE_URLS: `http://localhost:${port}`,
  ConnectionStrings__Default: `Server=${server};Database=${db};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=False`,
  Cache__UseRedis: 'false',
  Reconciliation__Enabled: 'false',
  Database__MigrateOnStartup: 'true',
  Database__Seed: 'true',
};
const child = spawn('dotnet', ['run', '--project', path.join(root, 'src', 'StockAccount.Api'), '--no-launch-profile'], { env, stdio: 'inherit', cwd: root });
const stop = () => { if (!child.killed) child.kill(); };
process.on('SIGINT', stop);
process.on('SIGTERM', stop);
process.on('exit', stop);
child.on('exit', (code) => process.exit(code ?? 0));
