#!/usr/bin/env bash
# Shared target selection for tests/prod-readiness/*.sh (source it, then: target_init "$@"; set -- "${TARGET_ARGS[@]}").
#
# DEFAULT TARGET = STAGING (compose project stockaccount-staging, https://localhost:8443, self-signed local CA => curl -k).
# These checks are aggressive (bursts of failed logins, stopping mssql/redis, real backup + restore). They REFUSE to run against
# the default dev project (stockaccount, ports 5080/5173/3000) unless you pass the explicit flag  --i-know-this-is-dev
# (or TARGET=dev together with the flag). Other options: --target staging|dev.
#
# Exports: TARGET PROJECT BASE_URL API UI_VUE_URL UI_NUXT_URL HEALTH_PREFIX CURL_K NET C_API C_MSSQL C_REDIS C_UI_VUE C_UI_NUXT
#          LOGIN_ADMIN_EMAIL LOGIN_ADMIN_PW LOGIN_OP_EMAIL LOGIN_OP_PW (staging: the bootstrap admin from .env.staging, both roles)
#          NODE_TLS_REJECT_UNAUTHORIZED=0 for staging (local CA).

TARGET=${TARGET:-staging}
DEV_OK=0
TARGET_ARGS=()
target_init() {
  TARGET_ARGS=()
  while (($#)); do
    case "$1" in
      --i-know-this-is-dev) DEV_OK=1 ;;
      --target) shift; TARGET=${1:-staging} ;;
      --target=*) TARGET=${1#--target=} ;;
      *) TARGET_ARGS+=("$1") ;;
    esac
    shift
  done
  local root; root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
  case "$TARGET" in
    staging)
      PROJECT=stockaccount-staging
      BASE_URL=${BASE_URL:-https://localhost:8443}
      API=${API:-$BASE_URL}; UI_VUE_URL=${UI_VUE_URL:-$BASE_URL}; UI_NUXT_URL=${UI_NUXT_URL:-}
      HEALTH_PREFIX=/api            # through Caddy -> nginx the API health endpoints live under /api
      CURL_K=-k; export NODE_TLS_REJECT_UNAUTHORIZED=0
      NET=${NET:-stockaccount-staging_default}
      local envf="$root/.env.staging"
      LOGIN_ADMIN_EMAIL=${LOGIN_ADMIN_EMAIL:-$(sed -n 's/^BOOTSTRAP_ADMIN_EMAIL=//p' "$envf" 2>/dev/null | tr -d '\r')}
      LOGIN_ADMIN_PW=${LOGIN_ADMIN_PW:-$(sed -n 's/^BOOTSTRAP_ADMIN_PASSWORD=//p' "$envf" 2>/dev/null | tr -d '\r')}
      LOGIN_OP_EMAIL=${LOGIN_OP_EMAIL:-$LOGIN_ADMIN_EMAIL}; LOGIN_OP_PW=${LOGIN_OP_PW:-$LOGIN_ADMIN_PW}
      ;;
    dev)
      if [[ $DEV_OK -ne 1 ]]; then
        echo "REFUSED: target 'dev' is the default compose project (stockaccount, ports 5080/5173/3000) of the developer stack." >&2
        echo "These checks send failed-login bursts, stop containers and restore backups. Use the staging stack (default), or pass --i-know-this-is-dev." >&2
        exit 2
      fi
      PROJECT=stockaccount
      API=${API:-http://127.0.0.1:5080}; BASE_URL=$API
      UI_VUE_URL=${UI_VUE_URL:-http://127.0.0.1:5173}; UI_NUXT_URL=${UI_NUXT_URL:-http://127.0.0.1:3000}
      HEALTH_PREFIX=; CURL_K=; NET=${NET:-stockaccount_default}
      LOGIN_ADMIN_EMAIL=admin@stockaccount.local; LOGIN_ADMIN_PW='Admin123!'; LOGIN_OP_EMAIL=demo@stockaccount.local; LOGIN_OP_PW='Demo123!'
      ;;
    *) echo "unknown --target '$TARGET' (staging|dev)" >&2; exit 2 ;;
  esac
  # Belt and braces: an explicit API/BASE_URL override pointing at the dev ports is also refused without the flag.
  if [[ $DEV_OK -ne 1 && "$API$BASE_URL$UI_VUE_URL" =~ (localhost|127\.0\.0\.1|\[::1\]):(5080|5173|3000)([^0-9]|$) ]]; then
    echo "REFUSED: the URL points at the dev stack ports (5080/5173/3000). Pass --i-know-this-is-dev if that is really intended." >&2
    exit 2
  fi
  C_API=${PROJECT}-api-1; C_MSSQL=${PROJECT}-mssql-1; C_REDIS=${PROJECT}-redis-1; C_UI_VUE=${PROJECT}-ui-vue-1; C_UI_NUXT=${PROJECT}-ui-nuxt-1
  export TARGET PROJECT BASE_URL API UI_VUE_URL UI_NUXT_URL HEALTH_PREFIX CURL_K NET C_API C_MSSQL C_REDIS C_UI_VUE C_UI_NUXT \
         LOGIN_ADMIN_EMAIL LOGIN_ADMIN_PW LOGIN_OP_EMAIL LOGIN_OP_PW
  echo "[target] $TARGET (project $PROJECT, $BASE_URL)" >&2
}
