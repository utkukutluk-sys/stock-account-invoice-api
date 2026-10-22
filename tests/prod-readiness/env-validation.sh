#!/usr/bin/env bash
# Round 3 item 2: fail-fast startup validation of the API and ui-nuxt images (Production), secrets never logged,
# and `docker compose config` failing on missing .env values. Usage: bash env-validation.sh [api-image] [nuxt-image]
set -u
export PATH="$PATH:/c/Users/PC/AppData/Local/Programs/DockerDesktop/resources/bin"
API_IMAGE=${1:-stockaccount-api:1.4.0}
NUXT_IMAGE=${2:-stockaccount-ui-nuxt:1.4.0}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
SECRET_PW='LeakPw-7f3e9!Zq'          # must never appear in any log line
SECRET_KEY='LEAKKEY-0123456789-abcdefghijklmnopqrstuvwxyz-XYZ'   # 50 bytes, valid length
SHORT_KEY='LEAKSHORT-123'
GOOD_CS="Server=mssql,1433;Database=StockAccountDb;User Id=stockaccount_app;Password=$SECRET_PW;TrustServerCertificate=True"
SA_CS="Server=mssql,1433;Database=StockAccountDb;User Id=sa;Password=$SECRET_PW;TrustServerCertificate=True"
pass=0; fail=0
run_case() { # name expected_exit expected_text -- docker run args...
  local name=$1 want=$2 text=$3; shift 3
  local cname="sa-envcheck-$RANDOM"
  local out; out=$(timeout 60 docker run --rm --name "$cname" "$@" 2>&1); local code=$?
  docker rm -f "$cname" >/dev/null 2>&1  # timeout kills the CLI, not the container
  local leak=no
  if grep -qF "$SECRET_PW" <<<"$out" || grep -qF "$SECRET_KEY" <<<"$out" || grep -qF "$SHORT_KEY" <<<"$out"; then leak=YES; fi
  local ok=FAIL
  if [[ "$code" == "$want" ]] && grep -qiF "$text" <<<"$out" && [[ $leak == no ]]; then ok=PASS; pass=$((pass+1)); else fail=$((fail+1)); fi
  printf '%-4s | %-58s | exit %-3s (want %s) | secret leaked: %-3s | %s\n' "$ok" "$name" "$code" "$want" "$leak" "$(grep -iE 'FTL|fatal|invalid|error' <<<"$out" | head -1 | cut -c1-160)"
}
P=(-e ASPNETCORE_ENVIRONMENT=Production -e Cache__UseRedis=false -e Database__MigrateOnStartup=false)
echo "== API image $API_IMAGE (Production) =="
run_case "Jwt:Key missing"                        2 "Jwt:Key is missing"     "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" -e Jwt__Key= "$API_IMAGE"
run_case "Jwt:Key placeholder (change-me)"        2 "placeholder"            "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" -e Jwt__Key=docker-change-me-0123456789-abcdefghijklmnopqrst "$API_IMAGE"
run_case "Jwt:Key image default (DEV-ONLY)"       2 "placeholder"            "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" "$API_IMAGE"
run_case "Jwt:Key too short (value not logged)"   2 "too short"              "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" -e "Jwt__Key=$SHORT_KEY" "$API_IMAGE"
run_case "ConnectionStrings:Default empty"        2 "ConnectionStrings:Default is missing" "${P[@]}" -e ConnectionStrings__Default= -e "Jwt__Key=$SECRET_KEY" "$API_IMAGE"
run_case "runtime login is sa (pw not logged)"    2 "must not use the 'sa'"  "${P[@]}" -e "ConnectionStrings__Default=$SA_CS" -e "Jwt__Key=$SECRET_KEY" "$API_IMAGE"
run_case "Redis enabled, ConnectionStrings:Redis empty" 2 "ConnectionStrings:Redis is missing" -e ASPNETCORE_ENVIRONMENT=Production -e Cache__UseRedis=true -e ConnectionStrings__Redis= -e "ConnectionStrings__Default=$GOOD_CS" -e "Jwt__Key=$SECRET_KEY" "$API_IMAGE"
run_case "ForwardedHeaders 0.0.0.0/0"             2 "must not trust every address" "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" -e "Jwt__Key=$SECRET_KEY" -e ForwardedHeaders__KnownNetworks__0=0.0.0.0/0 "$API_IMAGE"
run_case "ForwardedHeaders ::/0"                  2 "must not trust every address" "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" -e "Jwt__Key=$SECRET_KEY" -e ForwardedHeaders__KnownNetworks__0=::/0 "$API_IMAGE"
run_case "ForwardedHeaders 0.0.0.0/1 + 128.0.0.0/1 (trust-all split)" 2 "must not trust" "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" -e "Jwt__Key=$SECRET_KEY" -e ForwardedHeaders__KnownNetworks__0=0.0.0.0/1 -e ForwardedHeaders__KnownNetworks__1=128.0.0.0/1 "$API_IMAGE"
run_case "Cors origin *"                          2 "Cors:AllowedOrigins"    "${P[@]}" -e "ConnectionStrings__Default=$GOOD_CS" -e "Jwt__Key=$SECRET_KEY" -e "Cors__AllowedOrigins__0=*" "$API_IMAGE"
run_case "several errors reported at once"        2 "Jwt:Key is missing"     "${P[@]}" -e ConnectionStrings__Default= -e Jwt__Key= "$API_IMAGE"
echo "== ui-nuxt image $NUXT_IMAGE =="
run_case "NUXT_API_BASE missing"                  1 "NUXT_API_BASE"          -e NUXT_API_BASE= "$NUXT_IMAGE"
run_case "NUXT_API_BASE not absolute"             1 "NUXT_API_BASE"          -e NUXT_API_BASE=api:8080 "$NUXT_IMAGE"
run_case "NUXT_API_BASE ends with /api"           1 "NUXT_API_BASE"          -e NUXT_API_BASE=http://api:8080/api "$NUXT_IMAGE"
echo "== docker compose config with missing .env values =="
tmp=$(mktemp -d)
for missing in JWT_KEY MSSQL_SA_PASSWORD APP_DB_PASSWORD; do
  { echo "JWT_KEY=x$RANDOM"; echo "MSSQL_SA_PASSWORD=x$RANDOM"; echo "APP_DB_PASSWORD=x$RANDOM"; } | grep -v "^$missing=" > "$tmp/env"
  out=$(env -u JWT_KEY -u MSSQL_SA_PASSWORD -u APP_DB_PASSWORD docker compose -f "$ROOT/docker-compose.yml" --env-file "$tmp/env" config 2>&1 >/dev/null); code=$?
  if [[ $code -ne 0 ]] && grep -q "$missing" <<<"$out"; then r=PASS; pass=$((pass+1)); else r=FAIL; fail=$((fail+1)); fi
  printf '%-4s | compose config without %-18s | exit %s | %s\n' "$r" "$missing" "$code" "$(head -1 <<<"$out" | cut -c1-140)"
done
rm -rf "$tmp"
echo "TOTAL pass=$pass fail=$fail"
