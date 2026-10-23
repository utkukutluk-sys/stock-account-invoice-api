#!/usr/bin/env bash
# Round 4: BUG-7 (published ports are loopback only), BUG-8 (page overflow), BUG-9 (standalone restore flushes the cache),
# BUG-10 (415/405 are problem+json with a code). Target: STAGING by default (https://localhost:8443); the dev stack needs --i-know-this-is-dev.
# BUG-9 does a real backup + restore of the shared DB (it creates and removes one stock "R4STALE"). Usage: bash round4-checks.sh
set -u
source "$(dirname "${BASH_SOURCE[0]}")/_target.sh"; target_init "$@"; set -- "${TARGET_ARGS[@]}"
export PATH="$PATH:/c/Users/PC/AppData/Local/Programs/DockerDesktop/resources/bin"
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
pass=0; fail=0
chk() { if [[ $2 == "$3" ]]; then pass=$((pass+1)); echo "PASS | $1 | $2"; else fail=$((fail+1)); echo "FAIL | $1 | got '$2' want '$3'"; fi; }
login() { curl -s $CURL_K -X POST "$API/api/auth/login" -H 'Content-Type: application/json' -d "{\"email\":\"$1\",\"password\":\"$2\"}" | sed -E 's/.*"accessToken":"([^"]+)".*/\1/'; }
T=$(login "$LOGIN_ADMIN_EMAIL" "$LOGIN_ADMIN_PW")
H=(-H "Authorization: Bearer $T" -H "X-Company-Id: 1")

echo "== BUG-7 port bindings =="
ports=$(docker ps --filter "name=^${C_API}$" --format '{{.Ports}}')
chk "api published only on loopback" "$(grep -c '0.0.0.0:5080\|\[::\]:5080' <<<"$ports")" 0
for s in mssql redis; do p=$(docker ps --filter "name=^${PROJECT}-$s-1$" --format '{{.Ports}}'); chk "$s published only on loopback" "$(grep -c '0.0.0.0\|\[::\]:' <<<"$p")" 0; done
chk "api reachable on 127.0.0.1" "$(curl -s $CURL_K -o /dev/null -w '%{http_code}' $API$HEALTH_PREFIX/health/live)" 200

echo "== BUG-8 page overflow =="
for ep in stocks invoices stock-trans users; do
  chk "GET /api/$ep?page=2147483647" "$(curl -s $CURL_K -o /tmp/r4b -w '%{http_code}' "${H[@]}" "$API/api/$ep?page=2147483647&pageSize=100")" 400
done
chk "page=2147483647 code" "$(grep -o '"code":"[A-Z_]*"' /tmp/r4b | head -1)" '"code":"VALIDATION_FAILED"'
chk "page=1000001 -> 400" "$(curl -s $CURL_K -o /dev/null -w '%{http_code}' "${H[@]}" "$API/api/stocks?page=1000001&pageSize=100")" 400
chk "page=1000000 -> 200" "$(curl -s $CURL_K -o /dev/null -w '%{http_code}' "${H[@]}" "$API/api/stocks?page=1000000&pageSize=100")" 200

echo "== BUG-10 415 / 405 =="
for ct in text/plain application/xml application/x-www-form-urlencoded; do
  out=$(curl -s $CURL_K -D /tmp/r4h -o /tmp/r4b -w '%{http_code}' -X POST "${H[@]}" -H "Content-Type: $ct" -d 'x' "$API/api/stocks")
  chk "POST /api/stocks as $ct status" "$out" 415
  chk "  content-type" "$(grep -i '^content-type' /tmp/r4h | tr -d '\r' | cut -d';' -f1 | tr 'A-Z' 'a-z')" "content-type: application/problem+json"
  chk "  code" "$(grep -o '"code":"[A-Z_]*"' /tmp/r4b)" '"code":"UNSUPPORTED_MEDIA_TYPE"'
done
out=$(curl -s $CURL_K -D /tmp/r4h -o /tmp/r4b -w '%{http_code}' -X POST "${H[@]}" "$API/api/auth/refresh" -H 'Content-Type: text/plain' -d x)
chk "POST /api/auth/refresh text/plain status" "$out" 415
chk "  content-type" "$(grep -i '^content-type' /tmp/r4h | tr -d '\r' | cut -d';' -f1 | tr 'A-Z' 'a-z')" "content-type: application/problem+json"
out=$(curl -s $CURL_K -D /tmp/r4h -o /tmp/r4b -w '%{http_code}' -X DELETE "${H[@]}" "$API/api/auth/login")
chk "DELETE /api/auth/login status" "$out" 405
chk "  content-type" "$(grep -i '^content-type' /tmp/r4h | tr -d '\r' | cut -d';' -f1 | tr 'A-Z' 'a-z')" "content-type: application/problem+json"
chk "  code" "$(grep -o '"code":"[A-Z_]*"' /tmp/r4b)" '"code":"METHOD_NOT_ALLOWED"'

echo "== BUG-9 standalone restore flushes the cache =="
PS=powershell; ps_run() { powershell -NoProfile -ExecutionPolicy Bypass -File "$@" -Environment "$TARGET"; }
ps_run "$ROOT/scripts/db/backup.ps1" -Target docker -Label r4stale >/tmp/r4bak.log 2>&1; chk "backup exit" "$?" 0
code="R4S$RANDOM"
id=$(curl -s $CURL_K -X POST "${H[@]}" -H 'Content-Type: application/json' -d "{\"code\":\"$code\",\"name\":\"r4 stale\",\"unit\":\"Kg\",\"vatRate\":1,\"unitPrice\":1,\"isActive\":true}" "$API/api/stocks" | grep -o '"id":[0-9]*' | head -1 | cut -d: -f2)
echo "created stock $code id=$id"; [[ -n $id ]] || { echo "FAIL | stock create returned no id"; exit 1; }
curl -s $CURL_K -o /dev/null "${H[@]}" "$API/api/stocks/$id"
chk "second read before restore is a cache HIT" "$(curl -s $CURL_K -D - -o /dev/null "${H[@]}" "$API/api/stocks/$id" | grep -i '^x-cache' | tr -d '\r' | awk '{print toupper($2)}')" HIT
ps_run "$ROOT/scripts/db/restore.ps1" -Target docker -Latest -Label r4stale -Force >/tmp/r4rest.log 2>&1; chk "restore exit" "$?" 0
grep -E 'RESTORE OK|cache flushed' /tmp/r4rest.log | cut -c1-200
T=$(login "$LOGIN_ADMIN_EMAIL" "$LOGIN_ADMIN_PW"); H=(-H "Authorization: Bearer $T" -H "X-Company-Id: 1")
r=$(curl -s $CURL_K -D /tmp/r4h -o /dev/null -w '%{http_code}' "${H[@]}" "$API/api/stocks/$id")
chk "immediate GET after restore" "$r" 404
xc=$(grep -i "^x-cache" /tmp/r4h | tr -d '\r' | awk '{print toupper($2)}'); chk "  X-Cache is not HIT (404 carries MISS or no header)" "$([[ $xc == HIT ]] && echo HIT || echo ok)" ok
chk "list does not contain $code" "$(curl -s $CURL_K "${H[@]}" "$API/api/stocks?page=1&pageSize=100&search=$code" | grep -c "$code")" 0
echo "TOTAL pass=$pass fail=$fail"
