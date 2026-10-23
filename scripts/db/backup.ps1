<#
.SYNOPSIS
  Full backup of StockAccountDb with CHECKSUM, RESTORE VERIFYONLY, a JSON manifest and retention.

.DESCRIPTION
  Targets:
    -Target local  : .\SQLEXPRESS (Windows auth). Files go to <repo>\backups\local (the SQL Server service
                     account is granted Modify on that folder automatically).
    -Target docker : the compose service "mssql" (sa, password from $env:MSSQL_SA_PASSWORD or .env, never printed).
                     Files go to /var/opt/mssql/backup in the container = <repo>\backups on the host when the
                     ./backups bind mount exists; otherwise the file is copied out with `docker compose cp`.

  File name : <db>_<target>_<yyyyMMdd-HHmmss>Z_<label>.bak (+ .json manifest next to it).
  Options   : COPY_ONLY by default (ad-hoc backups never disturb a differential/log chain; use -ChainBase for a
              regular full backup), CHECKSUM always, COMPRESSION when the edition supports it (not on Express).
  Environments: -Environment dev|staging|prod (default $env:STOCKACCOUNT_ENV, else dev). dev writes to <repo>\backups (unchanged),
              staging to <repo>\backups\staging, prod to BACKUP_DIR or <repo>\backups\prod. Backup, restore and retention only
              operate inside that directory (guarded): a staging/prod retention can never delete dev files.
  Retention : keeps the newest -RetentionCount backups per label family ("pre-deploy-1.2.0" -> "pre-deploy");
              0 disables pruning.
  Exit code : 0 success, 1 any failure. The manifest object is written to the pipeline on success.

.EXAMPLE
  ./scripts/db/backup.ps1 -Target docker -Label pre-deploy-1.2.0
.EXAMPLE
  ./scripts/db/backup.ps1 -Target local -RetentionCount 7
#>
[CmdletBinding()]
param(
    [ValidateSet('local', 'docker')][string]$Target = 'local',
    [string]$Database = 'StockAccountDb',
    [string]$Label = 'manual',
    [ValidateRange(0, 1000)][int]$RetentionCount = 14,
    [string]$BackupDir,
    [string]$ServerInstance = '.\SQLEXPRESS',
    [string]$ComposeFile,
    [string]$Service = 'mssql',
    [switch]$ChainBase,
    [switch]$Offsite,
    [switch]$SkipOffsite,
    [string]$LogFile,
    # dev (default, files in ./backups) | staging (./backups/staging) | prod (BACKUP_DIR or ./backups/prod). Defaults to $env:STOCKACCOUNT_ENV.
    [ValidateSet('dev', 'staging', 'prod')][string]$Environment
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')
if ($Environment) { $null = Use-StockEnvironment $Environment }

$transcript = $false
try {
    if ($LogFile) { Start-Transcript -LiteralPath $LogFile -Append | Out-Null; $transcript = $true }

    Assert-Pattern $Label $script:LabelPattern 'label'
    $ctx = New-DbContext -Target $Target -Database $Database -ServerInstance $ServerInstance -ComposeFile $ComposeFile -Service $Service

    $state = Get-DbState $ctx
    if (-not $state.Exists) { throw "Database '$Database' does not exist on target '$Target'." }
    if ($state.State -ne 'ONLINE') { throw "Database '$Database' is $($state.State); only ONLINE databases can be backed up." }

    $compression = $state.EngineEdition -in 2, 3   # 2 = Standard, 3 = Enterprise/Developer; Express (4) has no compression
    if (-not $compression) { Write-Step "Edition '$($state.Edition)' does not support backup compression; backing up uncompressed." }
    if ($state.Recovery -eq 'FULL' -and -not $ChainBase) {
        Write-Step "Recovery model FULL: using COPY_ONLY. Note: without log backups the log only truncates via LOG_BACKUP (log_reuse_wait=$($state.LogReuseWait))."
    }

    $loc = Get-BackupLocation -Ctx $ctx -BackupDir $BackupDir -ForWrite
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $fileName = "{0}_{1}_{2}Z_{3}.bak" -f $Database, $Target, $stamp, $Label
    $serverPath = Join-ServerPath $loc $fileName
    $hostPath = Join-Path $loc.HostDir $fileName

    $with = @()
    if (-not $ChainBase) { $with += 'COPY_ONLY' }
    $with += 'CHECKSUM', 'INIT', 'FORMAT', 'STATS = 25'
    if ($compression) { $with += 'COMPRESSION' }
    $with += ('NAME = ' + (ConvertTo-SqlLiteral "$Database full $stamp $Label"))

    $db = ConvertTo-SqlIdentifier $Database
    $disk = ConvertTo-SqlLiteral $serverPath
    Write-Step "Backing up [$Database] ($Target) -> $fileName"
    $started = Get-Date
    $out = Invoke-DbSql -Ctx $ctx -Sql "BACKUP DATABASE $db TO DISK = $disk WITH $($with -join ', ');"
    $out | Where-Object { $_ -clike 'BACKUP DATABASE*' } | ForEach-Object { Write-Step $_ }

    Write-Step 'Verifying (RESTORE VERIFYONLY WITH CHECKSUM)'
    $verify = Invoke-DbSql -Ctx $ctx -Sql "SET NOCOUNT ON; RESTORE VERIFYONLY FROM DISK = $disk WITH CHECKSUM; SELECT 'VERIFY=OK';"
    $checksumOk = ((ConvertFrom-KeyValueLines $verify)['VERIFY'] -eq 'OK')
    if (-not $checksumOk) { throw "RESTORE VERIFYONLY did not succeed for $fileName" }

    $info = ConvertFrom-KeyValueLines (Invoke-DbSql -Ctx $ctx -Sql @"
SET NOCOUNT ON;
SELECT TOP (1) 'BACKUP_SIZE=' + CONVERT(varchar(30), CONVERT(bigint, b.backup_size)),
       'COMPRESSED_SIZE=' + CONVERT(varchar(30), CONVERT(bigint, b.compressed_backup_size)),
       'FINISHED=' + CONVERT(varchar(30), b.backup_finish_date, 126),
       'HAS_CHECKSUMS=' + CONVERT(varchar(1), b.has_backup_checksums)
FROM msdb.dbo.backupset AS b
JOIN msdb.dbo.backupmediafamily AS m ON m.media_set_id = b.media_set_id
WHERE m.physical_device_name = $disk
ORDER BY b.backup_set_id DESC;
"@)

    if ($Target -eq 'docker' -and -not $loc.Mounted) {
        Write-Step "./backups is not mounted into the container; copying the file out with docker compose cp"
        Invoke-ComposeCp -Ctx $ctx -From "$($ctx.Service):$serverPath" -To $hostPath
        Invoke-InContainer -Ctx $ctx -Command @('rm', '-f', $serverPath) | Out-Null
    }
    if (-not (Test-Path -LiteralPath $hostPath)) { throw "Backup file not found on the host: $hostPath" }

    $item = Get-Item -LiteralPath $hostPath
    $sha = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $manifest = [ordered]@{
        file               = $fileName
        database           = $Database
        target             = $Target
        label              = $Label
        labelFamily        = (Get-LabelFamily $Label)
        createdAtUtc       = $started.ToUniversalTime().ToString('o')
        durationSeconds    = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
        sizeBytes          = $item.Length
        sha256             = $sha
        checksumOk         = $checksumOk
        hasBackupChecksums = ($info['HAS_CHECKSUMS'] -eq '1')
        copyOnly           = (-not $ChainBase)
        compressed         = $compression
        migrationHead      = $state.MigrationHead
        migrationCount     = $(if ($state.MigrationCount) { [int]$state.MigrationCount } else { $null })
        recoveryModel      = $state.Recovery
        serverVersion      = $state.Version
        serverEdition      = $state.Edition
        rowCounts          = $state.RowCounts
        hostPath           = $hostPath
        serverPath         = $serverPath
    }
    $manifestPath = Get-ManifestPath $hostPath
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Write-Step ("OK {0} ({1:N1} MB, sha256 {2}..., migration head {3})" -f $fileName, ($item.Length / 1MB), $sha.Substring(0, 12), $state.MigrationHead)

    if ($RetentionCount -gt 0) {
        # Only inside this environment's own backup directory (guarded in Invoke-BackupRetention).
        $null = Invoke-BackupRetention -HostDir $loc.HostDir -Database $Database -Target $Target -Family (Get-LabelFamily $Label) -Keep $RetentionCount
    }


    # ---- off-site copy (rclone -> S3-compatible bucket), after the local backup has fully succeeded ----
    $exitAfter = 0
    $implicit = (-not $SkipOffsite) -and (-not $Offsite) -and (Test-OffsiteEnabled) -and ((Get-LabelFamily $Label) -cne 'pre-restore')
    if ($Offsite -or $implicit) {
        Write-Step 'Off-site copy (offsite-sync.ps1)'
        $null = & (Join-Path $PSScriptRoot 'offsite-sync.ps1') -BackupDir $loc.HostDir
        if ($LASTEXITCODE -ne 0) {
            if ($Offsite) {
                # Requested explicitly (scheduled task / cron): the local backup is fine, but the job must show up as failed.
                Write-Host "[db] OFF-SITE COPY FAILED (offsite-sync.ps1 exit $LASTEXITCODE); the local backup $fileName is intact. Fix the bucket settings and run scripts/db/offsite-sync.ps1." -ForegroundColor Red
                $exitAfter = 5
            }
            else {
                # Implicit (OFFSITE_ENABLED): never block callers such as deploy.ps1 / restore.ps1 on a bucket outage.
                Write-Host "[db] WARNING: off-site copy failed (exit $LASTEXITCODE); the local backup is intact. Run scripts/db/offsite-sync.ps1 to retry." -ForegroundColor Yellow
            }
        }
    }
    [pscustomobject]$manifest
    $exitCode = $exitAfter
}
catch {
    Write-Host "[db] BACKUP FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($transcript) { Stop-Transcript | Out-Null }
}
exit $exitCode
