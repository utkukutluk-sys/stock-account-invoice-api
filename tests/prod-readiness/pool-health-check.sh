#!/usr/bin/env bash
# Round 3 item 4: effective connection strings, SqlClient pool under 300 parallel requests (sessions ≤ Max Pool, no
# timeouts), /health/live vs /health/ready with SQL or Redis stopped, docker health status. Runs on the compose stack.
set -u
source "$(dirname "${BASH_SOURCE[0]}")/_target.sh"; target_init "$@"; set -- "${TARGET_ARGS[@]}"
export PATH="$PATH:/c/Users/PC/AppData/Local/Programs/DockerDesktop/resources/bin"
HERE=$(cd "$(dirname "$0")" && pwd)
sql() { docker exec $C_MSSQL bash -c "/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P \"\$MSSQL_SA_PASSWORD\" -h -1 -W -s '|' -Q \"SET NOCOUNT ON; $1\""; }
health() { curl -s $CURL_K -o /tmp/h.json -w '%{http_code}' -m 20 "$API$HEALTH_PREFIX$1"; echo " $(tr -d '\n' </tmp/h.json | cut -c1-200)"; }
dstate() { docker inspect -f '{{.Name}} {{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$@" | tr '\n' ';'; echo; }

echo "== effective runtime connection string (password masked) =="
docker inspect $C_API -f '{{range .Config.Env}}{{println .}}{{end}}' | grep '^ConnectionStrings__Default=' | sed -E 's/Password=[^;]*/Password=***/'
echo "== sessions per program/login before load =="
sql "SELECT program_name, login_name, COUNT(*) FROM sys.dm_exec_sessions WHERE database_id = DB_ID('StockAccountDb') GROUP BY program_name, login_name"

echo "== 300 parallel API requests (dashboard summary + invoice list, two users) =="
( for i in $(seq 1 40); do sql "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = 'StockAccount.Api'" | tr -d ' ' ; sleep 0.25; done ) > /tmp/sessions.txt &
sampler=$!
node "$HERE/parallel-load.mjs" "$API" 300
wait $sampler
echo "StockAccount.Api sessions sampled during load: max=$(sort -n /tmp/sessions.txt | tail -1) min=$(sort -n /tmp/sessions.txt | head -1) samples=$(wc -l </tmp/sessions.txt)"
sql "SELECT 'pool-timeouts-in-log', 0" >/dev/null
echo "pool timeout lines in api log (last 5 min): $(docker logs --since 5m $C_API 2>&1 | grep -ciE 'obtaining a connection from the pool|Timeout expired')"

echo "== health with everything up =="
echo "live:  $(health /health/live)"; echo "ready: $(health /health/ready)"
echo "== SQL Server stopped =="
docker stop $C_MSSQL >/dev/null; sleep 3
echo "live:  $(health /health/live)"; echo "ready: $(health /health/ready)"
echo "data request: $(curl -s $CURL_K -o /dev/null -w '%{http_code} in %{time_total}s' -m 60 "$API/api/stocks")  (anonymous → 401 without DB is fine)"
sleep 35; echo "docker after 35 s: $(dstate $C_API $C_UI_VUE $C_UI_NUXT)"
docker start $C_MSSQL >/dev/null
t0=$(date +%s); until [[ $(curl -s $CURL_K -o /dev/null -w '%{http_code}' -m 10 "$API$HEALTH_PREFIX/health/ready") == 200 ]]; do sleep 2; [[ $(( $(date +%s) - t0 )) -gt 180 ]] && break; done
echo "ready again after $(( $(date +%s) - t0 )) s: $(health /health/ready)"
echo "== Redis stopped =="
docker stop $C_REDIS >/dev/null; sleep 3
echo "live:  $(health /health/live)"; echo "ready: $(health /health/ready)"
TOKEN=$(curl -s $CURL_K -X POST "$API/api/auth/login" -H 'Content-Type: application/json' -d "{\"email\":\"$LOGIN_OP_EMAIL\",\"password\":\"$LOGIN_OP_PW\"}" | sed 's/.*"accessToken":"\([^"]*\)".*/\1/')
for i in 1 2 3; do echo "GET /api/stocks with Redis down: $(curl -s $CURL_K -o /dev/null -w '%{http_code} X-Cache=%header{x-cache} %{time_total}s' -H "Authorization: Bearer $TOKEN" "$API/api/stocks")"; done
sleep 30; echo "docker: $(dstate $C_API)"
docker start $C_REDIS >/dev/null; sleep 8
echo "ready after Redis start: $(health /health/ready)"
echo "final: $(dstate $C_MSSQL $C_REDIS $C_API $C_UI_VUE $C_UI_NUXT)"
