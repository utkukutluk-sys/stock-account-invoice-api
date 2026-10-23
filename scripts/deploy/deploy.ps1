<#
.SYNOPSIS
  Versioned deploy of the compose stack with a pre-deploy database backup and automatic rollback.

.DESCRIPTION
  1. Pre-deploy backup via scripts/db/backup.ps1 -Target docker -Label pre-deploy-<version>. A failed backup aborts the
     deploy. Skipped only for a genuine first deploy (no mssql container and no mssql-data volume) or with -SkipBackup. A stopped mssql is started first.
  2. Builds api/ui-vue/ui-nuxt images tagged <name>:<version> and <name>:sha-<git sha>.
  3. Records the release in scripts/deploy/.state/releases.json (git-ignored).
  4. docker compose up -d (-Production: with docker-compose.prod.yml) with APP_VERSION=<version> (mssql/redis keep running; pinned images).
  5. Waits for /health/ready (200) served by the new api image. On failure: automatic rollback to the previous version
     including the database restore from the pre-deploy backup (unless -NoAutoRollback).
  Exit code 0 = deployed, 1 = failed (rolled back if possible).

.EXAMPLE
  ./scripts/deploy/deploy.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [int]$ReadyTimeoutSeconds = 240,
    # Default: http://localhost:5080/health/ready (dev stack); with -Production the api container is asked from inside
    # the compose network ('internal': there are no host ports). Pass 'internal' or any URL to override.
    [string]$HealthUrl,
    # Production on a VPS: merges docker-compose.prod.yml (Production env, no host ports, Caddy + Uptime Kuma, no ui-nuxt).
    [switch]$Production,
    [switch]$SkipBackup,
    [switch]$NoAutoRollback,
    # Extra compose file merged over docker-compose.yml for THIS deploy only (e.g. scripts/deploy/drills/crash-api.override.yml
    # to drill the automatic rollback with a container that really crashes). The rollback always uses the plain compose file.
    [string[]]$ComposeOverride,
    # For releases that change the compose topology (networks, fixed IPs): after the backup and the build, run
    # "docker compose down --remove-orphans" (volumes are KEPT) before "up". A running stack blocks network recreation.
    [switch]$RecreateStack
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_deploy-common.ps1')

if ($Version -cnotmatch $script:VersionPattern) { throw "Invalid version '$Version' (expected e.g. 1.2.0)." }
if (-not (Test-Path -LiteralPath (Join-Path $script:RepoRoot '.env'))) { throw '.env not found: run scripts/deploy/new-secrets.ps1 first.' }

if (-not $HealthUrl) { $HealthUrl = if ($Production) { 'internal' } else { 'http://localhost:5080/health/ready' } }
if ($Production) {
    foreach ($key in 'DOMAIN') {
        if (-not (Get-EnvSetting $key)) { throw "$key is not set in .env (public host name, e.g. app.example.com). See docs/DEPLOYMENT.md." }
    }
    if (-not (Get-EnvSetting 'ACME_EMAIL') -and -not (Get-EnvSetting 'LOCAL_TLS')) { throw 'ACME_EMAIL is not set in .env (Lets Encrypt e-mail). See docs/DEPLOYMENT.md.' }
}
$extraFiles = @()
foreach ($o in @($ComposeOverride)) {
    foreach ($part in ($o -split ';')) {
        if ($part.Trim()) { $extraFiles += (Resolve-Path -LiteralPath (Join-Path $script:RepoRoot $part.Trim()) -ErrorAction Stop).Path }
    }
}
Set-ComposeFiles -ProductionMode:$Production -Extra $extraFiles
if ($Production) { Write-Deploy 'PRODUCTION mode: docker-compose.yml + docker-compose.prod.yml' 'Yellow' }
if ($extraFiles.Count -gt 0) { Write-Deploy "compose override(s) for this deploy: $($ComposeOverride -join ', ')" 'Yellow' }
$history = Read-History
$previous = $history.current
$sha = Get-GitSha
$backupFile = $null
$started = Get-Date
Write-Deploy "deploying $Version (git $sha); currently live: $(if ($previous) { $previous } else { '<none>' })"

try {
    # ---- 1. pre-deploy backup
    if ($SkipBackup) {
        Write-Deploy 'backup skipped (-SkipBackup)' 'Yellow'
    }
    elseif (-not (Test-DatabaseStackExists)) {
        Write-Deploy 'no mssql container and no mssql-data volume exist (genuine first deploy): no database to back up' 'Yellow'
    }
    else {
        # The DB exists (container in any state, or at least its volume): it must be running+healthy to be backed up.
        # A stopped mssql is NOT a first deploy - start it and wait (never skip the backup silently).
        try { Assert-DataServicesUp }
        catch { throw "mssql exists but could not be started for the pre-deploy backup - deploy aborted, nothing was changed: $($_.Exception.Message)" }
        $backupScript = [IO.Path]::Combine($script:RepoRoot, 'scripts', 'db', 'backup.ps1')
        if (-not (Test-Path -LiteralPath $backupScript)) { throw "backup script missing: $backupScript" }
        Write-Deploy "pre-deploy backup (label pre-deploy-$Version)"
        $manifest = $null
        try { $manifest = & $backupScript -Target docker -Label "pre-deploy-$Version" } catch { Write-Deploy "backup error: $($_.Exception.Message)" 'Red' }
        if ($LASTEXITCODE -ne 0 -or -not $manifest) { throw 'pre-deploy backup FAILED - deploy aborted, nothing was changed (use -SkipBackup only if you accept the risk).' }
        $backupFile = @($manifest)[-1].file
        Write-Deploy "backup OK: $backupFile" 'Green'
    }
    # ---- 2. build + tag
    $env:APP_VERSION = $Version
    Write-Deploy 'building images'
    Invoke-Docker (@('compose', 'build') + $script:AppServices) | Out-Null
    foreach ($image in $script:AppImages) {
        Invoke-Docker @('tag', "${image}:$Version", "${image}:sha-$sha") -Quiet | Out-Null
    }
    Write-Deploy "tagged $($script:AppImages -join ', ') as :$Version and :sha-$sha"

    # ---- 2b. topology change detection (BUG-12): a changed network / mssql / redis definition makes "compose up" recreate the
    #          stateful services and then fail with "network has active endpoints". Detect it up front and use the recreate path.
    # A dry-run "up" waits for health of containers that do not exist yet (hangs on a fresh server): only check an existing stack.
    $topology = @(if ($null -ne (Get-ServiceState 'mssql')) { Get-TopologyChangeReasons })
    if ($topology.Count -gt 0 -and -not $RecreateStack) {
        Write-Deploy 'compose topology change detected - using the recreate path automatically (volumes kept):' 'Yellow'
        $topology | ForEach-Object { Write-Deploy "  $_" 'Yellow' }
        $RecreateStack = [switch]$true
    }

    # ---- 3. record
    Add-HistoryEvent $history @{ version = $Version; sha = $sha; status = 'deploying'; previous = $previous; preDeployBackup = $backupFile }
    Save-History $history

    # ---- 4. up
    if ($RecreateStack) {
        Write-Deploy 'recreating the stack (compose down --remove-orphans, volumes kept)' 'Yellow'
        Invoke-Docker @('compose', 'down', '--remove-orphans') | Out-Null
    }
    Invoke-ComposeUpWithRetry

    # ---- 5. readiness
    if (-not (Wait-Ready -Version $Version -HealthUrl $HealthUrl -TimeoutSeconds $ReadyTimeoutSeconds)) {
        throw "$Version did not become ready within $ReadyTimeoutSeconds s ($HealthUrl)."
    }

    $history.current = $Version
    Add-HistoryEvent $history @{ version = $Version; sha = $sha; status = 'deployed'; previous = $previous; preDeployBackup = $backupFile }
    Save-History $history
    Write-Deploy ("DEPLOYED {0} in {1:N0} s (previous: {2}; pre-deploy backup: {3})" -f $Version, ((Get-Date) - $started).TotalSeconds, $previous, $backupFile) 'Green'
    exit 0
}
catch {
    Write-Deploy "DEPLOY FAILED: $($_.Exception.Message)" 'Red'
    $history = Read-History
    Add-HistoryEvent $history @{ version = $Version; sha = $sha; status = 'failed'; previous = $previous; preDeployBackup = $backupFile; error = $_.Exception.Message }
    Save-History $history

    Set-ComposeFiles -ProductionMode:$Production # rollback uses the plain compose file(s), never the drill overrides
    if (-not $NoAutoRollback -and $previous -and $previous -cne $Version -and (Test-ImageExists "stockaccount-api:$previous")) {
        Write-Deploy "automatic rollback to $previous" 'Yellow'
        $rollbackArgs = @{ Version = $previous; From = $Version; Reason ="auto: deploy of $Version failed"; HealthUrl = $HealthUrl }
        if ($Production) { $rollbackArgs.Production = $true }
        if ($backupFile) { $rollbackArgs.RestoreDatabase = $true; $rollbackArgs.BackupFile = $backupFile }
        & (Join-Path $PSScriptRoot 'rollback.ps1') @rollbackArgs
        if ($LASTEXITCODE -ne 0) { Write-Deploy 'AUTOMATIC ROLLBACK FAILED - manual intervention required' 'Red' }
    }
    exit 1
}
