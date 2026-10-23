# Shared helpers for scripts/deploy/*.ps1 (dot-sourced). Windows PowerShell 5.1 and PowerShell 7 (Windows + Linux) compatible:
# no backslash paths (Join-Path only), no Windows-only cmdlets, ordinal comparisons.
Set-StrictMode -Version 3.0

$script:RepoRoot    = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
# DEPLOY_STATE_DIR / COMPOSE_PROJECT_NAME let a second stack (e.g. a local production test) keep its own release history.
$script:StateDir    = if ($env:DEPLOY_STATE_DIR) { $env:DEPLOY_STATE_DIR } else { Join-Path $PSScriptRoot '.state' }
$script:HistoryFile = Join-Path $script:StateDir 'releases.json'
$script:ComposeProject = if ($env:COMPOSE_PROJECT_NAME) { $env:COMPOSE_PROJECT_NAME } else { 'stockaccount' }
$script:ProdMode  = $false
$script:AppImages   = @('stockaccount-api', 'stockaccount-ui-vue', 'stockaccount-ui-nuxt')
$script:AppServices = @('api', 'ui-vue', 'ui-nuxt')
# Use -cmatch/-ceq: PowerShell -match/-eq are culture-sensitive and fail on "I" under tr-TR.
$script:VersionPattern = '^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.]+)?$'

function Write-Deploy([string]$Message, [string]$Color = 'Cyan') { Write-Host "[deploy] $Message" -ForegroundColor $Color }

# Production mode: docker-compose.prod.yml is merged over docker-compose.yml for EVERY compose call (deploy, rollback, backup
# helpers), ui-nuxt is not part of the release and readiness is checked inside the compose network (no host ports in prod).
# $Extra = additional compose files for this deploy only (drills).
function Set-ComposeFiles([switch]$ProductionMode, [string[]]$Extra = @()) {
    $script:ProdMode = [bool]$ProductionMode
    $files = @((Join-Path $script:RepoRoot 'docker-compose.yml'))
    if ($script:ProdMode) { $files += (Join-Path $script:RepoRoot 'docker-compose.prod.yml') }
    $files += $Extra
    foreach ($f in $files) { if (-not (Test-Path -LiteralPath $f)) { throw "compose file not found: $f" } }
    if ($script:ProdMode -or $Extra.Count -gt 0) { $env:COMPOSE_FILE = $files -join [IO.Path]::PathSeparator }
    else { Remove-Item Env:COMPOSE_FILE -ErrorAction SilentlyContinue }
    if ($script:ProdMode) {
        $script:AppImages   = @('stockaccount-api', 'stockaccount-ui-vue')
        $script:AppServices = @('api', 'ui-vue')
    }
}

# Value of a setting: process environment first (compose gives it precedence too), then the repo's .env file. $null when empty.
function Get-EnvSetting([string]$Name) {
    $fromEnv = [Environment]::GetEnvironmentVariable($Name)
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) { return $fromEnv }
    $envFile = Join-Path $script:RepoRoot '.env'
    if (Test-Path -LiteralPath $envFile) {
        foreach ($line in [IO.File]::ReadAllLines($envFile)) {
            if ($line -cmatch ('^\s*' + [regex]::Escape($Name) + '\s*=\s*(.*?)\s*$') -and -not [string]::IsNullOrWhiteSpace($Matches[1])) { return $Matches[1] }
        }
    }
    return $null
}

function Get-DockerExe {
    $candidates = @()
    if ($env:DOCKER_EXE) { $candidates += $env:DOCKER_EXE }
    $cmd = Get-Command docker -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    if ($env:LOCALAPPDATA) { $candidates += [IO.Path]::Combine($env:LOCALAPPDATA, 'Programs', 'DockerDesktop', 'resources', 'bin', 'docker.exe') }
    if ($env:ProgramFiles) { $candidates += [IO.Path]::Combine($env:ProgramFiles, 'Docker', 'Docker', 'resources', 'bin', 'docker.exe') }
    foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return $c } }
    throw 'docker CLI not found (set DOCKER_EXE or add docker to PATH).'
}

# Runs docker with the repo as working directory; throws on a non-zero exit code unless -AllowFailure.
function Invoke-Docker([string[]]$Arguments, [switch]$AllowFailure, [switch]$Quiet) {
    $docker = Get-DockerExe
    Push-Location $script:RepoRoot
    try {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $output = & $docker @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
        $script:LastDockerExit = $code
        $ErrorActionPreference = $prev
    }
    finally { Pop-Location }
    if (-not $Quiet) { $output | ForEach-Object { Write-Host "  $_" } }
    if ($code -ne 0 -and -not $AllowFailure) { throw "docker $($Arguments -join ' ') failed with exit code $code" }
    return , @($output)
}

function Read-History {
    if (-not (Test-Path -LiteralPath $script:HistoryFile)) {
        return [pscustomobject]@{ current = $null; releases = @() }
    }
    $h = Get-Content -LiteralPath $script:HistoryFile -Raw | ConvertFrom-Json
    if (-not $h.releases) { $h.releases = @() }
    return $h
}

function Save-History($History) {
    if (-not (Test-Path -LiteralPath $script:StateDir)) { New-Item -ItemType Directory -Path $script:StateDir | Out-Null }
    # Remember the mode so "rollback.ps1" in a fresh shell uses the same compose files without being told.
    $History | Add-Member -NotePropertyName production -NotePropertyValue $script:ProdMode -Force
    $json = $History | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($script:HistoryFile, $json, (New-Object Text.UTF8Encoding($false)))
}

function Add-HistoryEvent($History, [hashtable]$Event) {
    $Event['atUtc'] = (Get-Date).ToUniversalTime().ToString('o')
    $History.releases = @($History.releases) + @([pscustomobject]$Event)
}

# Version that was live immediately before $Current (last successful deploy/rollback to a different version).
function Get-PreviousVersion($History, [string]$Current) {
    $live = @($History.releases | Where-Object { $_.status -cin @('deployed', 'rolled-back-to') } | ForEach-Object { $_.version })
    for ($i = $live.Count - 1; $i -ge 0; $i--) {
        if ($live[$i] -cne $Current) { return $live[$i] }
    }
    return $null
}

function Test-ImageExists([string]$Image) {
    $out = Invoke-Docker @('image', 'inspect', '--format', '{{.Id}}', $Image) -AllowFailure -Quiet
    return ($script:LastDockerExit -eq 0 -and ($out -join "") -cmatch "^sha256:")
}

function Test-ServiceRunning([string]$Service) {
    $id = (Invoke-Docker @('compose', 'ps', '-q', '--status', 'running', $Service) -AllowFailure -Quiet) -join ''
    return -not [string]::IsNullOrWhiteSpace($id)
}

# Polls the readiness endpoint until 200 and the api container runs the expected image tag.
# Production has no host ports: ask the api container itself (bash /dev/tcp, the image has no curl) for /health/ready.
function Get-InternalReadyStatus {
    $probe = "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3 && head -n1 <&3"
    $out = Invoke-Docker @('compose', 'exec', '-T', 'api', 'bash', '-c', $probe) -AllowFailure -Quiet
    return (($out -join ' ').Trim())
}

function Wait-Ready([string]$Version, [string]$HealthUrl, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $expectedImage = "stockaccount-api:$Version"
    while ((Get-Date) -lt $deadline) {
        $cid = ((Invoke-Docker @('compose', 'ps', '-q', 'api') -AllowFailure -Quiet) -join '').Trim()
        if ($cid) {
            $image = ((Invoke-Docker @('inspect', '--format', '{{.Config.Image}}', $cid) -AllowFailure -Quiet) -join '').Trim()
            if ($image -ceq $expectedImage) {
                if ($HealthUrl -ceq 'internal') {
                    $status = Get-InternalReadyStatus
                    if ($status -cmatch ' 200( |$)') { Write-Deploy "ready: api container /health/ready -> $status" 'Green'; return $true }
                }
                else {
                    try {
                        $res = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 5
                        if ($res.StatusCode -eq 200) {
                            Write-Deploy "ready: $HealthUrl -> 200 $($res.Content)" 'Green'
                            return $true
                        }
                    }
                    catch { }
                }
            }
        }
        Start-Sleep -Seconds 3
    }
    return $false
}

function Get-GitSha {
    Push-Location $script:RepoRoot
    try {
        $sha = (& git rev-parse --short=12 HEAD 2>$null)
        if (-not $sha) { return 'nogit' }
        $dirty = (& git status --porcelain -- src docker-compose.yml 2>$null)
        if ($dirty) { $sha = "$sha-dirty" }
        return $sha
    }
    finally { Pop-Location }
}

# ---------------------------------------------------------------------------------------------------------------
# Data-service helpers (BUG-12). All comparisons are ordinal (-c*): tr-TR breaks -match/-eq with 'I'/'i'.
# ---------------------------------------------------------------------------------------------------------------
$script:DataServices = @('mssql', 'redis')

# "Running" / "Created" / "Exited" ... + health ("healthy", "starting", "unhealthy" or '' without a healthcheck); $null = no container.
function Get-ServiceState([string]$Service) {
    $cid = ((Invoke-Docker @('compose', 'ps', '-a', '-q', $Service) -AllowFailure -Quiet) -join '').Trim()
    if (-not $cid) { return $null }
    $fmt = '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}'
    $line = ((Invoke-Docker @('inspect', '--format', $fmt, $cid) -AllowFailure -Quiet) -join '').Trim()
    if ($script:LastDockerExit -ne 0 -or -not $line) { return $null }
    $parts = $line.Split('|')
    return [pscustomobject]@{ Status = $parts[0]; Health = $(if ($parts.Count -gt 1) { $parts[1] } else { '' }) }
}

function Test-ServiceHealthy([string]$Service) {
    $s = Get-ServiceState $Service
    return ($null -ne $s -and $s.Status -ceq 'running' -and ($s.Health -ceq 'healthy' -or $s.Health -ceq ''))
}

function Wait-ServiceHealthy([string]$Service, [int]$TimeoutSeconds = 180) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-ServiceHealthy $Service) { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

# True when the stateful DB data already exists (container of any state, or the named volume).
function Test-DatabaseStackExists {
    if ($null -ne (Get-ServiceState 'mssql')) { return $true }
    $vol = ((Invoke-Docker @('volume', 'ls', '-q', '--filter', "label=com.docker.compose.project=$($script:ComposeProject)", '--filter', 'label=com.docker.compose.volume=mssql-data') -AllowFailure -Quiet) -join '').Trim()
    return [bool]$vol
}

# Makes sure mssql and redis run and are healthy. Starts them when needed; if "compose up" fails because the old network
# still has active endpoints (half-recreated stack), does ONE "compose down --remove-orphans" (volumes kept) + up.
# Throws with an actionable message otherwise. Does nothing when both are already healthy.
function Assert-DataServicesUp([int]$TimeoutSeconds = 180) {
    if ((Test-ServiceHealthy 'mssql') -and (Test-ServiceHealthy 'redis')) { return }
    Write-Deploy 'mssql/redis not both running+healthy: starting them' 'Yellow'
    $out = Invoke-Docker (@('compose', 'up', '-d', '--no-build') + $script:DataServices) -AllowFailure
    if ($script:LastDockerExit -ne 0) {
        if (($out -join "`n") -cmatch 'active endpoints') {
            Write-Deploy 'network has active endpoints: one compose down --remove-orphans (volumes kept), then up again' 'Yellow'
            Invoke-Docker @('compose', 'down', '--remove-orphans') | Out-Null
            Invoke-Docker (@('compose', 'up', '-d', '--no-build') + $script:DataServices) | Out-Null
        }
        else { throw "could not start mssql/redis: $(($out | Select-Object -Last 3) -join ' | ')" }
    }
    foreach ($svc in $script:DataServices) {
        if (-not (Wait-ServiceHealthy $svc $TimeoutSeconds)) {
            throw "$svc did not become healthy within $TimeoutSeconds s (check: docker compose logs $svc)."
        }
    }
    Write-Deploy 'mssql and redis are running and healthy' 'Green'
}

# Dry-run of "compose up": returns human-readable reasons when the running stateful services (mssql/redis) would be
# recreated or a network replaced/removed (compose then fails with "network ... has active endpoints"). Empty = safe.
function Get-TopologyChangeReasons {
    $out = Invoke-Docker @('compose', 'up', '-d', '--no-build', '--dry-run') -AllowFailure -Quiet
    if ($script:LastDockerExit -ne 0) {
        return @("compose up --dry-run failed ($(($out | Select-Object -Last 2) -join ' | ')): assuming topology change")
    }
    $reasons = @()
    foreach ($line in $out) {
        if ($line -cmatch 'Container\s+\S*(mssql|redis)\S*\s+Recreate\b') { $reasons += $line.Trim() }
        elseif ($line -cmatch 'Network\s+\S+\s+(Removing|Recreate)\b') { $reasons += $line.Trim() }
    }
    return @($reasons)
}

# "compose up -d --no-build" (whole stack). On "network ... has active endpoints" does one compose down --remove-orphans
# (volumes kept) and retries once. Throws on any other failure.
function Invoke-ComposeUpWithRetry {
    $out = Invoke-Docker @('compose', 'up', '-d', '--no-build') -AllowFailure
    if ($script:LastDockerExit -eq 0) { return }
    if (($out -join "`n") -cnotmatch 'active endpoints') { throw "docker compose up failed (exit $($script:LastDockerExit))." }
    Write-Deploy 'network has active endpoints: one compose down --remove-orphans (volumes kept), then up again' 'Yellow'
    Invoke-Docker @('compose', 'down', '--remove-orphans') | Out-Null
    Invoke-Docker @('compose', 'up', '-d', '--no-build') | Out-Null
}
