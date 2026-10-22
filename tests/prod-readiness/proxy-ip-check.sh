#!/usr/bin/env bash
# Round 3 item 1b: through ui-vue nginx and the ui-nuxt Nitro proxy, two different client IPs get separate rate-limit
# buckets and a client-supplied X-Forwarded-For cannot buy a fresh bucket. Also probes the API port directly.
# Runs against the compose stack (Docker profile: auth limit 100/60 s per IP). Client 1 = this host, client 2 = a
# container on the compose network (node:24-alpine). Each phase waits for the 60 s window to expire first.
set -u
export PATH="$PATH:/c/Users/PC/AppData/Local/Programs/DockerDesktop/resources/bin"
NET=${NET:-stockaccount_default}
LIMIT=${LIMIT:-100}
BODY='{"email":"nobody@ratelimit.local","password":"Wrong12345"}'
host_burst() { # url [xff] → prints status histogram of LIMIT+1 failed logins
  local url=$1 xff=${2:-} codes=()
  for _ in $(seq 0 "$LIMIT"); do
    if [[ -n $xff ]]; then codes+=("$(curl -s -o /dev/null -w '%{http_code}' -X POST "$url" -H 'Content-Type: application/json' -H "X-Forwarded-For: $xff" -d "$BODY")")
    else codes+=("$(curl -s -o /dev/null -w '%{http_code}' -X POST "$url" -H 'Content-Type: application/json' -d "$BODY")"); fi
  done
  printf '%s\n' "${codes[@]}" | sort | uniq -c | tr '\n' ' '; echo "(last=${codes[-1]})"
}
host_one() { curl -s -o /dev/null -w '%{http_code}' -X POST "$1" -H 'Content-Type: application/json' ${2:+-H "X-Forwarded-For: $2"} -d "$BODY"; }
container_one() { # url [xff] → status of one login from a container on the compose network
  docker run --rm --network "$NET" -e URL="$1" -e XFF="${2:-}" -e BODY="$BODY" node:24-alpine node -e '
    const h={"Content-Type":"application/json"}; if(process.env.XFF) h["X-Forwarded-For"]=process.env.XFF;
    fetch(process.env.URL,{method:"POST",headers:h,body:process.env.BODY}).then(r=>console.log(r.status)).catch(e=>console.log("ERR "+e.message));'
}
wait_window() { echo "  … waiting 65 s for the window to expire"; sleep 65; }

for phase in "ui-vue|http://localhost:5173/api/auth/login|http://ui-vue/api/auth/login" "ui-nuxt|http://localhost:3000/api/auth/login|http://ui-nuxt:3000/api/auth/login"; do
  IFS='|' read -r name hostUrl innerUrl <<<"$phase"
  wait_window
  echo "== $name =="
  echo "host burst ($((LIMIT+1)) logins):            $(host_burst "$hostUrl")"
  echo "host + spoofed XFF 203.0.113.7:         $(host_one "$hostUrl" 203.0.113.7)   (want 429)"
  echo "host + spoofed XFF chain 1.1.1.1, 2.2.2.2: $(host_one "$hostUrl" '1.1.1.1, 2.2.2.2')   (want 429)"
  echo "container client (other IP):            $(container_one "$innerUrl")   (want 401 = own bucket)"
  echo "container client + spoofed XFF:         $(container_one "$innerUrl" 198.51.100.9)   (want 401)"
done

wait_window
echo "== API port 5080 directly (no UI proxy) =="
echo "host burst:                              $(host_burst http://localhost:5080/api/auth/login)"
echo "host + spoofed XFF 203.0.113.50:          $(host_one http://localhost:5080/api/auth/login 203.0.113.50)   (want 429; 401 = bypass)"
echo "host + spoofed XFF 203.0.113.51:          $(host_one http://localhost:5080/api/auth/login 203.0.113.51)   (want 429; 401 = bypass)"
echo "spoofed XFF burst ($((LIMIT+1)) × new IP each):"
codes=(); for i in $(seq 0 "$LIMIT"); do codes+=("$(host_one http://localhost:5080/api/auth/login "198.18.$((i/250)).$((i%250+1))")"); done
echo "  $(printf '%s\n' "${codes[@]}" | sort | uniq -c | tr '\n' ' ')  (all 401 ⇒ unlimited brute force via XFF)"
