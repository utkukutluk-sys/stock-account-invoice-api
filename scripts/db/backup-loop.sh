#!/bin/bash
# Scheduled backup loop for the "mssql-backup" sidecar (scripts/db/docker-compose.backup.yml).
# Same file naming and manifest format as scripts/db/backup.ps1, label "scheduled", so restore.ps1 -Latest works:
#   <db>_docker_<yyyyMMdd-HHmmss>Z_scheduled.bak + .json   in /var/opt/mssql/backup (= ./backups on the host)
# The BACKUP statement runs inside the SQL Server container (it writes the file); this container only issues
# sqlcmd commands, writes the manifest and prunes old scheduled backups. Never prints the password.
set -u

DB="${DB_NAME:-StockAccountDb}"
DIR="/var/opt/mssql/backup"
HOST="${SQL_HOST:-mssql}"
INTERVAL="${BACKUP_INTERVAL:-24h}"
FIRST_DELAY="${BACKUP_FIRST_DELAY:-120s}"
KEEP="${BACKUP_RETENTION:-14}"
SQLCMD=/opt/mssql-tools18/bin/sqlcmd

[[ "$DB" =~ ^[A-Za-z_][A-Za-z0-9_]{0,127}$ ]] || { echo "[backup] invalid DB_NAME"; exit 1; }
[[ "$KEEP" =~ ^[0-9]{1,4}$ ]] || { echo "[backup] invalid BACKUP_RETENTION"; exit 1; }
[[ "$INTERVAL" =~ ^[0-9]+[smhd]?$ && "$FIRST_DELAY" =~ ^[0-9]+[smhd]?$ ]] || { echo "[backup] invalid interval"; exit 1; }
export SQLCMDPASSWORD="${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD is required}"

q()  { "$SQLCMD" -S "$HOST" -U sa -C -d master -b -I -h -1 -W -Q "$1"; }
qd() { "$SQLCMD" -S "$HOST" -U sa -C -d "$DB" -b -I -h -1 -W -s '|' -Q "$1"; }
log() { echo "[backup] $(date -u +%FT%TZ) $*"; }

run_once() {
    local ts name path ee comp head rows size sha created
    ts=$(date -u +%Y%m%d-%H%M%S)
    created=$(date -u +%FT%TZ)
    name="${DB}_docker_${ts}Z_scheduled.bak"
    path="${DIR}/${name}"

    ee=$(q "SET NOCOUNT ON; SELECT CONVERT(int, SERVERPROPERTY('EngineEdition'));" | tr -d '[:space:]') || return 1
    comp="false"; extra=""
    if [[ "$ee" == "2" || "$ee" == "3" ]]; then comp="true"; extra=", COMPRESSION"; fi

    q "BACKUP DATABASE [${DB}] TO DISK = N'${path}' WITH COPY_ONLY, CHECKSUM, INIT, FORMAT${extra};" >/dev/null || return 1
    q "RESTORE VERIFYONLY FROM DISK = N'${path}' WITH CHECKSUM;" >/dev/null || return 1

    head=$(qd "SET NOCOUNT ON; SELECT TOP (1) MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC;" | tr -d '[:space:]') || return 1
    rows=$(qd "SET NOCOUNT ON; SELECT s.name + '.' + t.name, SUM(ps.row_count) FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id JOIN sys.dm_db_partition_stats AS ps ON ps.object_id = t.object_id AND ps.index_id IN (0, 1) WHERE t.is_ms_shipped = 0 GROUP BY s.name, t.name ORDER BY s.name, t.name;" \
        | awk -F'|' 'NF==2 { printf "%s\"%s\": %s", (n++ ? ", " : ""), $1, $2 }') || return 1
    size=$(stat -c %s "$path")
    sha=$(sha256sum "$path" | cut -d' ' -f1)

    cat > "${path%.bak}.json" <<EOF
{
  "file": "${name}",
  "database": "${DB}",
  "target": "docker",
  "label": "scheduled",
  "labelFamily": "scheduled",
  "createdAtUtc": "${created}",
  "sizeBytes": ${size},
  "sha256": "${sha}",
  "checksumOk": true,
  "copyOnly": true,
  "compressed": ${comp},
  "migrationHead": "${head}",
  "rowCounts": { ${rows} },
  "serverPath": "${path}",
  "source": "mssql-backup sidecar"
}
EOF
    log "OK ${name} (${size} bytes, migration head ${head})"

    if [[ "$KEEP" -gt 0 ]]; then
        ls -1 "${DIR}/${DB}_docker_"*Z_scheduled.bak 2>/dev/null | sort -r | tail -n +$((KEEP + 1)) | while read -r old; do
            rm -f -- "$old" "${old%.bak}.json"
            log "retention (keep ${KEEP}): removed $(basename "$old")"
        done
    fi
}

log "sidecar started: db=${DB} host=${HOST} interval=${INTERVAL} keep=${KEEP}; first run in ${FIRST_DELAY}"
sleep "$FIRST_DELAY"
while true; do
    if ! run_once; then
        log "BACKUP FAILED (see sqlcmd output above); will retry in ${INTERVAL}"
    fi
    sleep "$INTERVAL"
done
