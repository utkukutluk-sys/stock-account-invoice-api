#!/usr/bin/env bash
# Round 3 item 1b: rate limiting per client IP behind the proxy chain. Two different client IPs get separate rate-limit buckets
# and a client-supplied X-Forwarded-For cannot buy a fresh bucket.
#
# DEFAULT TARGET = STAGING (production profile: auth limit 10 per 60 s per IP): chain  client -> Caddy (https://localhost:8443)
#   -> ui-vue nginx -> api. Client 1 = this host, client 2 = a throw-away container on the staging network that talks to ui-vue directly
#   (own source IP). The dev stack (auth limit 100, ui-vue :5173, ui-nuxt :3000, api :5080) needs --i-know-this-is-dev.
# Each phase waits for the 60 s window to expire first. Usage: bash proxy-ip-check.sh [--target staging|dev] [--i-know-this-is-dev]
set -u
source "$(dirname "${BASH_SOURCE[0]}")/_target.sh"; target_init "$@"; set -- "${TARGET_ARGS[@]}"
export PATH="$PATH:/c/Users/PC/AppData/Local/Programs/DockerDesktop/resources/bin"
BODY='{"email":"nobody@ratelimit.local","password":"Wrong12345"}'
if [[ $TARGET == staging ]]; then LIMIT=${LIMIT:-10}; else LIMIT=${LIMIT:-100}; fi
host_burst() { # url [xff] → prints status histogram of LIMIT+1 failed logins
  local url=$1 xff=${2:-} codes=()
  for _ in $(seq 0 "$LIMIT"); do
    if [[ -n $xff ]]; then codes+=("$(curl -s $CURL_K -o /dev/null -w '%{http_code}' -X POST "$url" -H 'Content-Type: application/json' -H "X-Forwarded-For: $xff" -d "$BODY")")
    else codes+=("$(curl -s $CURL_K -o /dev/null -w '%{http_code}' -X POST "$url" -H 'Content-Type: application/json' -d "$BODY")"); fi
  done
  printf '%s\n' "${codes[@]}" | sort | uniq -c | tr '\n' ' '; echo "(last=${codes[-1]})"
}
host_one() { curl -s $CURL_K -o /dev/null -w '%{http_code}' -X POST "$1" -H 'Content-Type: application/json' ${2:+-H "X-Forwarded-For: $2"} -d "$BODY"; }
container_one() { # url [xff] → status of one login from a container on the compose network
  docker run --rm --network "$NET" -e URL="$1" -e XFF="${2:-}" -e BODY="$BODY" node:24-alpine node -e '
    const h={"Content-Type":"application/json"}; if(process.env.XFF) h["X-Forwarded-For"]=process.env.XFF;
    fetch(process.env.URL,{method:"POST",headers:h,body:process.env.BODY}).then(r=>console.log(r.status)).catch(e=>console.log("ERR "+e.message));'
}
wait_window() { echo "  … waiting 65 s for the window to expire"; sleep 65; }

if [[ $TARGET == staging ]]; then
  PHASES=("caddy+ui-vue|$BASE_URL/api/auth/login|http://ui-vue/api/auth/login")
else
  PHASES=("ui-vue|$UI_VUE_URL/api/auth/login|http://ui-vue/api/auth/login" "ui-nuxt|$UI_NUXT_URL/api/auth/login|http://ui-nuxt:3000/api/auth/login")
fi
for phase in "${PHASES[@]}"; do
  IFS='|' read -r name hostUrl innerUrl <<<"$phase"
  wait_window
  echo "== $name (limit $LIMIT/60 s) =="
  echo "host burst ($((LIMIT+1)) logins):            $(host_burst "$hostUrl")   (want ${LIMIT}x401 then 429)"
  echo "host + spoofed XFF 203.0.113.7:         $(host_one "$hostUrl" 203.0.113.7)   (want 429)"
  echo "host + spoofed XFF chain 1.1.1.1, 2.2.2.2: $(host_one "$hostUrl" '1.1.1.1, 2.2.2.2')   (want 429)"
  echo "container client (other IP):            $(container_one "$innerUrl")   (want 401 = own bucket)"
  echo "container client + spoofed XFF:         $(container_one "$innerUrl" 198.51.100.9)   (want 401)"
done

if [[ $TARGET == staging ]]; then
  echo "== API port: staging publishes no api port =="
  echo "http://127.0.0.1:5080 is the DEV api, never probed here. Published ports of the staging api container: '$(docker ps --filter "name=^${C_API}\$" --format '{{.Ports}}' | grep -v '^8080/tcp$')' (want empty = only the internal 8080/tcp)"
else
  wait_window
  echo "== API port 5080 directly (no UI proxy) =="
  echo "host burst:                              $(host_burst "$API/api/auth/login")"
  echo "host + spoofed XFF 203.0.113.50:          $(host_one "$API/api/auth/login" 203.0.113.50)   (want 429; 401 = bypass)"
  echo "host + spoofed XFF 203.0.113.51:          $(host_one "$API/api/auth/login" 203.0.113.51)   (want 429; 401 = bypass)"
  echo "spoofed XFF burst ($((LIMIT+1)) × new IP each):"
  codes=(); for i in $(seq 0 "$LIMIT"); do codes+=("$(host_one "$API/api/auth/login" "198.18.$((i/250)).$((i%250+1))")"); done
  echo "  $(printf '%s\n' "${codes[@]}" | sort | uniq -c | tr '\n' ' ')  (all 401 ⇒ unlimited brute force via XFF)"
fi
