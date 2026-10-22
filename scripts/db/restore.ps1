<#
.SYNOPSIS
  Restores StockAccountDb from a backup made by backup.ps1 and verifies the result.

.DESCRIPTION
  Order of operations:
    1. Resolve the backup (-File <name|path> inside the backup folder, or -Latest [-Label <label|family>]).
       Check SHA-256 against the manifest.
    2. Ask for confirmation (type the database name). Without a console (-NonInteractive), -Force is required.
    3. Safety backup of the current database (label "pre-restore", no pruning) unless -NoSafetyBackup.
    4. RESTORE VERIFYONLY + FILELISTONLY. Then SINGLE_USER WITH ROLLBACK IMMEDIATE, OFFLINE (so pooled API
       connections cannot grab the database between the two steps), RESTORE ... WITH REPLACE, CHECKSUM, MOVE
       (files keep the current database's physical paths, or the instance defaults), then MULTI_USER.
       On failure the database is brought back ONLINE / MULTI_USER before the error is reported.
    5. Verify: DBCC CHECKDB WITH NO_INFOMSGS, ONLINE + MULTI_USER, migration head and per-table row counts
       equal to the manifest.
    6. Flush the API cache (BUG-9): every Redis key with the app prefix (default "sa:") is removed with
       SCAN + UNLINK in batches (never FLUSHALL). docker: via `docker compose exec redis redis-cli`.
       local: -RedisHost/-RedisPort (default localhost:6379, e.g. the sa-redis container) over a small RESP client.
       If Redis is not running/reachable, a warning says to restart the API (its in-memory fallback cache cannot
       be flushed from outside). Idempotent: running it again (e.g. rollback.ps1's own flush) just deletes 0 keys.
       -SkipCacheFlush skips this step (only when the caller flushes itself).
  Exit code: 0 success, 1 failure, 2 not confirmed, 3 database restored and verified but the cache flush FAILED
  (Redis reachable but returned an error): restart the API or flush sa:* by hand before serving traffic.
  The API keeps pooled connections that were killed by the restore; SqlClient discards them on first use and
  EF retries, but restarting the API after a restore is recommended (rollback.ps1 does).
  Schema rollback = restoring the pre-deploy backup; there are no EF down-migrations in this project.

.EXAMPLE
  ./scripts/db/restore.ps1 -Target docker -Latest -Label pre-deploy -Force
.EXAMPLE
  ./scripts/db/restore.ps1 -Target local -File StockAccountDb_local_20261022-084201Z_manual.bak
#>
[CmdletBinding(DefaultParameterSetName = 'File')]
param(
    [ValidateSet('local', 'docker')][string]$Target = 'local',
    [string]$Database = 'StockAccountDb',
    [Parameter(ParameterSetName = 'File', Mandatory)][string]$File,
    [Parameter(ParameterSetName = 'Latest', Mandatory)][switch]$Latest,
    [Parameter(ParameterSetName = 'Latest')][string]$Label,
    [switch]$Force,
    [switch]$NoSafetyBackup,
    [string]$BackupDir,
    [string]$ServerInstance = '.\SQLEXPRESS',
    [string]$ComposeFile,
    [string]$Service = 'mssql',
    [switch]$SkipCacheFlush,
    [string]$CachePrefix = 'sa:',
    [string]$RedisService = 'redis',
    [string]$RedisHost = 'localhost',
    [ValidateRange(1, 65535)][int]$RedisPort = 6379
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$exitCode = 1
$copiedIntoContainer = $null
$ctx = $null
try {
    $ctx = New-DbContext -Target $Target -Database $Database -ServerInstance $ServerInstance -ComposeFile $ComposeFile -Service $Service
    $loc = Get-BackupLocation -Ctx $ctx -BackupDir $BackupDir

    # ---- 1. resolve backup file ----
    if ($Latest) {
        $candidates = Get-BackupFiles -HostDir $loc.HostDir -Database $Database -Target $Target
        if ($Label) {
            Assert-Pattern $Label $script:LabelPattern 'label'
            $candidates = @($candidates | Where-Object { $_.Label -ceq $Label -or $_.Family -ceq $Label })
        }
        $candidates = @($candidates | Where-Object { $_.Family -cne 'pre-restore' -or $Label -ceq 'pre-restore' })
        if ($candidates.Count -eq 0) { throw "No backup of '$Database' ($Target) found in $($loc.HostDir)$(if ($Label) { " with label '$Label'" })." }
        $fileName = $candidates[0].File.Name
    }
    else {
        $fileName = Split-Path -Leaf $File
    }
    Assert-Pattern $fileName $script:BackupFilePattern 'backup file name'
    $hostPath = Join-Path $loc.HostDir $fileName
    if (-not (Test-Path -LiteralPath $hostPath)) { throw "Backup file not found: $hostPath (backups are restored only from $($loc.HostDir))." }

    $manifest = Read-Manifest $hostPath
    if ($manifest) {
        if ($manifest.database -cne $Database) { throw "Manifest says database '$($manifest.database)', not '$Database'." }
        $sha = (Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sha -cne $manifest.sha256) { throw "SHA-256 mismatch for $fileName (file changed or corrupted)." }
        Write-Step "Backup ${fileName}: label '$($manifest.label)', created $($manifest.createdAtUtc), migration head $($manifest.migrationHead), sha256 OK"
    }
    else {
        Write-Step "WARNING: no manifest for $fileName; migration head / row counts cannot be compared after restore."
    }

    # ---- 2. confirmation ----
    if (-not $Force) {
        if (-not (Test-Interactive)) { Write-Host '[db] Refusing to restore without -Force in non-interactive mode.' -ForegroundColor Red; exit 2 }
        $answer = Read-Host "This REPLACES database '$Database' on target '$Target' with $fileName. Type the database name to continue"
        if ($answer -cne $Database) { Write-Host '[db] Not confirmed; nothing changed.' -ForegroundColor Yellow; exit 2 }
    }

    $before = Get-DbState $ctx

    # ---- 3. safety backup ----
    if ($before.Exists -and $before.State -eq 'ONLINE' -and -not $NoSafetyBackup) {
        Write-Step 'Taking safety backup of the current database (label pre-restore)'
        $safetyArgs = @{ Target = $Target; Database = $Database; Label = 'pre-restore'; RetentionCount = 5; ServerInstance = $ServerInstance; Service = $Service }
        if ($BackupDir) { $safetyArgs.BackupDir = $BackupDir }
        if ($ComposeFile) { $safetyArgs.ComposeFile = $ComposeFile }
        $null = & (Join-Path $PSScriptRoot 'backup.ps1') @safetyArgs
        if ($LASTEXITCODE -ne 0) { throw 'Safety backup failed; restore aborted (use -NoSafetyBackup to skip it).' }
    }

    # ---- 4. make the file visible to the server ----
    $serverPath = Join-ServerPath $loc $fileName
    if ($Target -eq 'docker' -and -not $loc.Mounted) {
        Write-Step './backups is not mounted into the container; copying the file in with docker compose cp'
        Invoke-ComposeCp -Ctx $ctx -From $hostPath -To "$($ctx.Service):$serverPath"
        $copiedIntoContainer = $serverPath
    }
    $disk = ConvertTo-SqlLiteral $serverPath

    Write-Step 'RESTORE VERIFYONLY WITH CHECKSUM'
    $null = Invoke-DbSql -Ctx $ctx -Sql "RESTORE VERIFYONLY FROM DISK = $disk WITH CHECKSUM;"

    $fileList = Invoke-DbSql -Ctx $ctx -Sql "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = $disk;"
    $backupFiles = @(foreach ($l in $fileList) {
            $c = $l -split '\|'
            if ($c.Count -ge 3 -and ($c[2] -ceq 'D' -or $c[2] -ceq 'L')) { [pscustomobject]@{ Logical = $c[0]; Type = $c[2] } }
        })
    if ($backupFiles.Count -lt 2) { throw "Unexpected RESTORE FILELISTONLY output: $($fileList -join ' / ')" }

    # Target physical paths: keep the existing database's folders, else the instance defaults.
    $sep = $loc.Separator
    $existingData = @($before.Files | Where-Object { $_.Type -ceq 'D' })
    $existingLog = @($before.Files | Where-Object { $_.Type -ceq 'L' })
    $dataDir = if ($existingData.Count) { $existingData[0].Path.Substring(0, $existingData[0].Path.LastIndexOf($sep) + 1) } else { $before.DataPath }
    $logDir = if ($existingLog.Count) { $existingLog[0].Path.Substring(0, $existingLog[0].Path.LastIndexOf($sep) + 1) } else { $before.LogPath }
    if (-not $dataDir.EndsWith($sep)) { $dataDir += $sep }
    if (-not $logDir.EndsWith($sep)) { $logDir += $sep }

    $moves = @(); $d = 0; $lg = 0
    foreach ($f in $backupFiles) {
        Assert-Pattern $f.Logical '^[A-Za-z0-9_ .-]{1,128}$' 'logical file name'
        if ($f.Type -ceq 'D') {
            $phys = if ($d -eq 0) { "$dataDir$Database.mdf" } else { "$dataDir${Database}_$($f.Logical).ndf" }
            $d++
        }
        else {
            $phys = if ($lg -eq 0) { "$logDir${Database}_log.ldf" } else { "$logDir${Database}_$($f.Logical).ldf" }
            $lg++
        }
        Assert-Pattern $phys $script:SafePathPattern 'physical file path'
        $moves += "MOVE $(ConvertTo-SqlLiteral $f.Logical) TO $(ConvertTo-SqlLiteral $phys)"
    }

    $lit = ConvertTo-SqlLiteral $Database
    $id = ConvertTo-SqlIdentifier $Database
    $restoreSql = @"
SET NOCOUNT ON;
BEGIN TRY
    IF DB_ID($lit) IS NOT NULL
    BEGIN
        IF EXISTS (SELECT 1 FROM sys.databases WHERE name = $lit AND state_desc = 'ONLINE')
            ALTER DATABASE $id SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        ALTER DATABASE $id SET OFFLINE WITH ROLLBACK IMMEDIATE;
    END
    RESTORE DATABASE $id FROM DISK = $disk
        WITH REPLACE, CHECKSUM, RECOVERY, STATS = 25,
             $($moves -join ",`n             ");
    ALTER DATABASE $id SET MULTI_USER WITH ROLLBACK IMMEDIATE;
    SELECT 'RESTORE=OK';
END TRY
BEGIN CATCH
    IF DB_ID($lit) IS NOT NULL
    BEGIN TRY
        ALTER DATABASE $id SET ONLINE;
        ALTER DATABASE $id SET MULTI_USER WITH ROLLBACK IMMEDIATE;
    END TRY
    BEGIN CATCH
    END CATCH;
    THROW;
END CATCH
"@
    Write-Step "Restoring [$Database] from $fileName (SINGLE_USER -> OFFLINE -> RESTORE WITH REPLACE -> MULTI_USER)"
    $out = Invoke-DbSql -Ctx $ctx -Sql $restoreSql
    $out | Where-Object { $_ -clike 'RESTORE DATABASE*' } | ForEach-Object { Write-Step $_ }
    if ((ConvertFrom-KeyValueLines $out)['RESTORE'] -cne 'OK') { throw 'RESTORE did not report success.' }

    # ---- 5. verify ----
    Write-Step 'DBCC CHECKDB WITH NO_INFOMSGS'
    $chk = Invoke-DbSql -Ctx $ctx -Sql "SET NOCOUNT ON; DBCC CHECKDB($lit) WITH NO_INFOMSGS; SELECT 'CHECKDB=OK';"
    if ((ConvertFrom-KeyValueLines $chk)['CHECKDB'] -cne 'OK') { throw "DBCC CHECKDB reported problems: $($chk -join ' / ')" }

    $after = Get-DbState $ctx
    $problems = @()
    if ($after.State -ne 'ONLINE') { $problems += "state is $($after.State)" }
    if ($after.UserAccess -ne 'MULTI_USER') { $problems += "user access is $($after.UserAccess)" }
    if ($manifest) {
        if ("$($after.MigrationHead)" -cne "$($manifest.migrationHead)") { $problems += "migration head '$($after.MigrationHead)' != manifest '$($manifest.migrationHead)'" }
        foreach ($p in $manifest.rowCounts.PSObject.Properties) {
            $actual = if ($after.RowCounts.Contains($p.Name)) { $after.RowCounts[$p.Name] } else { $null }
            if ($actual -ne [long]$p.Value) { $problems += "rows $($p.Name): $actual != manifest $($p.Value)" }
        }
        foreach ($k in $after.RowCounts.Keys) {
            if (-not ($manifest.rowCounts.PSObject.Properties.Name -contains $k)) { $problems += "table $k not in manifest" }
        }
    }
    if ($problems.Count) { throw "Post-restore verification failed: $($problems -join '; ')" }

    $tables = $after.RowCounts.Count
    $totalRows = ($after.RowCounts.Values | Measure-Object -Sum).Sum
    Write-Step "RESTORE OK: [$Database] ONLINE/MULTI_USER, CHECKDB clean, migration head $($after.MigrationHead), $tables tables / $totalRows rows match the manifest"
    $exitCode = 0

    # ---- 6. API cache flush (BUG-9) ----
    if ($SkipCacheFlush) {
        Write-Step "Cache flush skipped (-SkipCacheFlush): the caller must remove '$CachePrefix*' keys or restart the API."
    }
    else {
        $flush = if ($Target -eq 'docker') { Clear-AppCacheDocker -Ctx $ctx -Prefix $CachePrefix -RedisService $RedisService }
                 else { Clear-AppCacheTcp -RedisHost $RedisHost -Port $RedisPort -Prefix $CachePrefix }
        switch ($flush.Status) {
            'Flushed' { Write-Step "API cache flushed: $($flush.Deleted) '$CachePrefix*' key(s) removed ($($flush.Message))" }
            'Unreachable' {
                Write-Host "[db] WARNING: API cache NOT flushed - $($flush.Message). If the API was using Redis or its in-memory fallback, restart the API now; otherwise it serves pre-restore data until the cache TTL (10 min by id, 5 min lists)." -ForegroundColor Yellow
            }
            default {
                Write-Host "[db] CACHE FLUSH FAILED after a successful restore: $($flush.Message). Restart the API or remove '$CachePrefix*' keys before serving traffic." -ForegroundColor Red
                $exitCode = 3
            }
        }
    }
}
catch {
    Write-Host "[db] RESTORE FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($copiedIntoContainer -and $ctx) {
        try { Invoke-InContainer -Ctx $ctx -Command @('rm', '-f', $copiedIntoContainer) -AllowFailure | Out-Null } catch { }
    }
}
exit $exitCode
