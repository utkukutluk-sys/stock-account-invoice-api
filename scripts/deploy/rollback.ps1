<#
.SYNOPSIS
  One-command rollback of the compose stack to the previous (or a given) release.

.DESCRIPTION
  - No arguments: rolls back to the version that was live before the current one (scripts/deploy/.state/releases.json).
  - -Version x.y.z: rolls back to that version (its images must still exist locally: <name>:x.y.z).
  - -RestoreDatabase: also restores the database from the pre-deploy backup taken right before the current release was
    deployed (or -BackupFile <name>.bak) via scripts/db/restore.ps1. This is how the schema goes back: EF down-migrations
    are not used, and the API refuses to start on a database that has migrations newer than its code.
  Steps: stop api/ui services -> (restore DB) -> compose up -d --no-build with APP_VERSION=<target> -> wait for
  /health/ready -> record. Exit code 0 = rolled back, 1 = failed.

.EXAMPLE
  ./scripts/deploy/rollback.ps1
.EXAMPLE
  ./scripts/deploy/rollback.ps1 -Version 1.1.0 -RestoreDatabase
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$RestoreDatabase,
    [string]$BackupFile,
    [int]$ReadyTimeoutSeconds = 240,
    [string]$HealthUrl = 'http://localhost:5080/health/ready',
    [string]$Reason = 'manual',
    # Version being replaced (deploy.ps1 passes the failed version); default: the recorded current release.
    [string]$From
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_deploy-common.ps1')

$history = Read-History
$current = if ($From) { $From } else { $history.current }
$started = Get-Date

try {
    if (-not $Version) {
        $Version = Get-PreviousVersion $history $current
        if (-not $Version) { throw 'No previous release recorded; pass -Version.' }
    }
    if ($Version -cnotmatch $script:VersionPattern) { throw "Invalid version '$Version'." }
    foreach ($image in $script:AppImages) {
        if (-not (Test-ImageExists "${image}:$Version")) { throw "image ${image}:$Version not found locally - cannot roll back to $Version." }
    }
    Write-Deploy "rolling back $(if ($current) { $current } else { '<unknown>' }) -> $Version (reason: $Reason)" 'Yellow'

    if ($RestoreDatabase) {
        if (-not $BackupFile) {
            # The backup taken right before the current release was deployed = the database state of the target release.
            $deployOfCurrent = @($history.releases | Where-Object { $_.version -ceq $current -and $_.status -ceq 'deployed' }) | Select-Object -Last 1
            if ($deployOfCurrent -and $deployOfCurrent.preDeployBackup) { $BackupFile = $deployOfCurrent.preDeployBackup }
        }
        if (-not $BackupFile) { throw 'No pre-deploy backup recorded for the current release; pass -BackupFile <name>.bak.' }

        $restoreScript = Join-Path $script:RepoRoot 'scripts\db\restore.ps1'
        if (-not (Test-Path -LiteralPath $restoreScript)) { throw "restore script missing: $restoreScript" }

        Write-Deploy "stopping application services before the restore"
        Invoke-Docker (@('compose', 'stop') + $script:AppServices) | Out-Null
        Write-Deploy "restoring database from $BackupFile"
        & $restoreScript -Target docker -File $BackupFile -Force
        if ($LASTEXITCODE -ne 0) { throw "database restore from $BackupFile FAILED" }
        Write-Deploy 'database restored' 'Green'

        # Cached DTOs/lists describe the pre-restore database: drop every API key (instance prefix sa:) so nothing stale is served.
        Write-Deploy 'flushing API cache keys (sa:*) in Redis'
        Invoke-Docker @('compose', 'exec', '-T', 'redis', 'sh', '-c', "redis-cli --scan --pattern 'sa:*' | xargs -r redis-cli del") | Out-Null
        if ($script:LastDockerExit -ne 0) { throw 'Redis cache flush failed after the database restore' }
    }

    $env:APP_VERSION = $Version
    Invoke-Docker @('compose', 'up', '-d', '--no-build') | Out-Null
    if (-not (Wait-Ready -Version $Version -HealthUrl $HealthUrl -TimeoutSeconds $ReadyTimeoutSeconds)) {
        throw "$Version did not become ready within $ReadyTimeoutSeconds s after rollback."
    }

    $history = Read-History
    $history.current = $Version
    Add-HistoryEvent $history @{ version = $Version; status = 'rolled-back-to'; from = $current; reason = $Reason; databaseRestoredFrom = $(if ($RestoreDatabase) { $BackupFile } else { $null }) }
    Save-History $history
    Write-Deploy ("ROLLED BACK to {0} in {1:N0} s{2}" -f $Version, ((Get-Date) - $started).TotalSeconds, $(if ($RestoreDatabase) { " (database restored from $BackupFile)" } else { '' })) 'Green'
    exit 0
}
catch {
    Write-Deploy "ROLLBACK FAILED: $($_.Exception.Message)" 'Red'
    exit 1
}
