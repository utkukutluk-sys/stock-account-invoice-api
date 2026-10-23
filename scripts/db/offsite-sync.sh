#!/bin/sh
# Off-site sync engine: runs INSIDE the pinned rclone/rclone container (busybox sh), either
#   * once, started by scripts/db/offsite-sync.ps1 (script text arrives on stdin, ./backups mounted at /data), or
#   * as a loop in the "offsite-sync" service of scripts/db/docker-compose.backup.yml.
# Config only through environment variables (never files, never printed):
#   RCLONE_CONFIG_OFFSITE_*  (type/endpoint/region/keys; built by the caller from OFFSITE_S3_*)
#   OFFSITE_S3_BUCKET, OFFSITE_PREFIX, OFFSITE_RETENTION_DAYS, OFFSITE_MIN_KEEP, OFFSITE_DATA_DIR (default /data)
# Modes: once | loop | list
# Exit: 0 ok, 1 sync/verify failure, 2 invalid configuration.
set -u

MODE="${1:-once}"
DATA="${OFFSITE_DATA_DIR:-/data}"
BUCKET="${OFFSITE_S3_BUCKET:-}"
PREFIX="${OFFSITE_PREFIX:-stockaccount}"
DAYS="${OFFSITE_RETENTION_DAYS:-30}"
MIN_KEEP="${OFFSITE_MIN_KEEP:-3}"
POLL="${OFFSITE_POLL_SECONDS:-60}"
FORCE_EVERY="${OFFSITE_FULL_CHECK_SECONDS:-21600}"

# rclone never reads stdin here (stdin carries this script when started by offsite-sync.ps1, or the retention pipe).
rclone() { command rclone "$@" </dev/null; }
log() { echo "[offsite] $(date -u +%FT%TZ) $*"; }
fail_cfg() { log "invalid configuration: $*"; exit 2; }

# rclone must not look for / print a config file; real settings come from RCLONE_CONFIG_OFFSITE_* only.
: > /tmp/rclone.conf
export RCLONE_CONFIG=/tmp/rclone.conf
# AWS proper: no endpoint, provider AWS. Cloudflare R2: provider Cloudflare. Everything else (B2, Hetzner, MinIO ...): Other.
if [ -z "${RCLONE_CONFIG_OFFSITE_ENDPOINT:-}" ]; then
    export RCLONE_CONFIG_OFFSITE_PROVIDER="${RCLONE_CONFIG_OFFSITE_PROVIDER:-AWS}"
elif printf '%s' "$RCLONE_CONFIG_OFFSITE_ENDPOINT" | grep -q '\.r2\.cloudflarestorage\.com'; then
    export RCLONE_CONFIG_OFFSITE_PROVIDER="${RCLONE_CONFIG_OFFSITE_PROVIDER:-Cloudflare}"
else
    export RCLONE_CONFIG_OFFSITE_PROVIDER="${RCLONE_CONFIG_OFFSITE_PROVIDER:-Other}"
fi
export RCLONE_CONFIG_OFFSITE_TYPE=s3
# Keys that may not create buckets (B2/R2 app keys) must not make rclone try.
export RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET=true

validate() {
    case "$BUCKET" in ''|*[!a-z0-9.-]*) fail_cfg "OFFSITE_S3_BUCKET missing or has invalid characters" ;; esac
    case "$PREFIX" in *[!A-Za-z0-9._/-]*|*..*|/*) fail_cfg "OFFSITE_PREFIX has invalid characters" ;; esac
    case "$DAYS" in ''|*[!0-9]*) fail_cfg "OFFSITE_RETENTION_DAYS must be a number" ;; esac
    case "$MIN_KEEP" in ''|*[!0-9]*) fail_cfg "OFFSITE_MIN_KEEP must be a number" ;; esac
    [ -n "${RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID:-}" ] && [ -n "${RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY:-}" ] \
        || fail_cfg "OFFSITE_S3_ACCESS_KEY / OFFSITE_S3_SECRET_KEY missing"
    PREFIX="${PREFIX%/}"
    if [ -n "$PREFIX" ]; then REMOTE="offsite:${BUCKET}/${PREFIX}"; else REMOTE="offsite:${BUCKET}"; fi
}

# A backup is complete when its manifest (.json) exists next to the .bak; the manifest is uploaded last.
build_lists() {
    : > /tmp/l.bak; : > /tmp/l.json
    for j in "$DATA"/*.json; do
        [ -f "$j" ] || continue
        b=$(basename "$j" .json)
        printf '%s' "$b" | grep -Eq '^[A-Za-z0-9_][A-Za-z0-9._-]{0,200}$' || continue
        [ -f "$DATA/$b.bak" ] || continue
        echo "$b.bak" >> /tmp/l.bak
        echo "$b.json" >> /tmp/l.json
    done
}

fingerprint() { ls -l $(sed "s#^#$DATA/#" /tmp/l.bak /tmp/l.json) 2>/dev/null | awk '{print $5, $9}' | cksum; }

upload_and_verify() {
    n=$(wc -l < /tmp/l.bak)
    [ "$n" -gt 0 ] || { log "no complete local backups (.bak + .json) found in $DATA"; return 0; }
    log "uploading up to $n backup(s) to bucket '${BUCKET}' prefix '${PREFIX}' (only missing/changed files are transferred)"
    rclone copy "$DATA" "$REMOTE" --files-from /tmp/l.bak --no-traverse --s3-no-check-bucket --retries 3 --log-level NOTICE --stats 0 || { log "FAILED: upload of .bak files"; return 1; }
    rclone copy "$DATA" "$REMOTE" --files-from /tmp/l.json --no-traverse --s3-no-check-bucket --retries 3 --log-level NOTICE --stats 0 || { log "FAILED: upload of manifests"; return 1; }
    # Verify: size + hash (MD5 recorded by S3 / rclone) of every listed file; any difference or missing remote file fails.
    cat /tmp/l.bak /tmp/l.json > /tmp/l.all
    rclone check "$DATA" "$REMOTE" --files-from /tmp/l.all --one-way --log-level NOTICE --combined /tmp/check.out >/tmp/check.err 2>&1
    rc=$?
    if [ "$rc" -ne 0 ]; then
        log "FAILED: verification (size/checksum) found differences:"
        grep -v '^= ' /tmp/check.out | head -20
        head -5 /tmp/check.err
        return 1
    fi
    ok=$(grep -c '^= ' /tmp/check.out)
    log "verified $ok file(s) on the remote (size + checksum)"
    return 0
}

retention() {
    [ "$DAYS" -gt 0 ] || { log "remote retention disabled (OFFSITE_RETENTION_DAYS=0)"; return 0; }
    now=$(date -u +%s)
    cutoff=$(date -u -d "@$((now - DAYS * 86400))" +%Y%m%d%H%M%S) || return 1
    rclone lsf "$REMOTE" --files-only --include '*.bak' --max-depth 1 --s3-no-check-bucket 2>/tmp/ret.err > /tmp/r.names || { log "FAILED: listing remote for retention"; head -3 /tmp/ret.err; return 1; }
    # "<stamp> <name>", newest first; the MIN_KEEP newest are never deleted, whatever their age.
    sed -n 's/^\(.*_\([0-9]\{8\}\)-\([0-9]\{6\}\)Z_.*\.bak\)$/\2\3 \1/p' /tmp/r.names | sort -r > /tmp/r.sorted
    total=$(wc -l < /tmp/r.sorted)
    removed=0
    tail -n +$((MIN_KEEP + 1)) /tmp/r.sorted | while read -r stamp name; do
        if [ "$stamp" -lt "$cutoff" ]; then
            base="${name%.bak}"
            rclone deletefile "$REMOTE/$name" --s3-no-check-bucket && rclone deletefile "$REMOTE/$base.json" --s3-no-check-bucket 2>/dev/null
            log "retention (>${DAYS}d, keep newest ${MIN_KEEP}): removed $name"
        fi
    done
    log "remote retention done ($total backup(s) on the remote before pruning)"
    return 0
}

sync_once() {
    build_lists
    upload_and_verify || return 1
    retention || return 1
    return 0
}

case "$MODE" in
    once)
        validate
        sync_once
        rc=$?
        [ "$rc" -eq 0 ] && log "OK" || log "FAILED"
        exit $rc
        ;;
    list)
        validate
        rclone lsf "$REMOTE" --files-only --include '*.bak' --max-depth 1 --s3-no-check-bucket | sort
        exit $?
        ;;
    loop)
        case "${OFFSITE_ENABLED:-false}" in
            true|True|TRUE|1|yes) ;;
            *) log "OFFSITE_ENABLED is not true; off-site sync idle (set OFFSITE_ENABLED=true in .env and recreate this service)"; exec sleep 2147483647 ;;
        esac
        validate
        log "started: bucket=${BUCKET} prefix=${PREFIX} retention=${DAYS}d min-keep=${MIN_KEEP} poll=${POLL}s"
        last=""; last_ok=0
        while true; do
            build_lists
            fp=$(fingerprint)
            now=$(date +%s)
            # Upload as soon as the set of completed local backups changes; re-verify + prune at least every FORCE_EVERY s.
            if [ "$fp" != "$last" ] || [ $((now - last_ok)) -ge "$FORCE_EVERY" ]; then
                if sync_once; then last="$fp"; last_ok=$now; else log "FAILED; will retry in ${POLL}s"; fi
            fi
            sleep "$POLL"
        done
        ;;
    *) fail_cfg "unknown mode '$MODE'" ;;
esac
