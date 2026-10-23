# Shared helpers for scripts/deploy/*.ps1 (dot-sourced). Windows PowerShell 5.1 and PowerShell 7 (Windows + Linux) compatible:
# no backslash paths (Join-Path only), no Windows-only cmdlets, ordinal comparisons.
Set-StrictMode -Version 3.0

$script:RepoRoot    = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
# dev | staging | prod: compose project, env file, release history and backup folder per environment (scripts/_environments.ps1).
. (Join-Path (Join-Path $PSScriptRoot '..') '_environments.ps1')
# Defaults = dev (historic behaviour); Initialize-DeployEnvironment switches them. DEPLOY_STATE_DIR still overrides the state folder
# (a second stack keeps its own history); COMPOSE_PROJECT_NAME still names a second dev-style stack.
$script:EnvDef = Get-EnvironmentDefinition 'dev'
$script:BaseStateDir = Join-Path $PSScriptRoot '.state'
$script:StateDir    = if ($env:DEPLOY_STATE_DIR) { $env:DEPLOY_STATE_DIR } else { $script:BaseStateDir }
$script:HistoryFile = Join-Path $script:StateDir 'releases.json'
$script:ComposeProject = if ($env:COMPOSE_PROJECT_NAME) { $env:COMPOSE_PROJECT_NAME } else { 'stockaccount' }
$script:ProdMode  = $false

$script:AppImages   = @('stockaccount-api', 'stockaccount-ui-vue', 'stockaccount-ui-nuxt')
$script:AppServices = @('api', 'ui-vue', 'ui-nuxt')
# Use -cmatch/-ceq: PowerShell -match/-eq are culture-sensitive and fail on "I" under tr-TR.
$script:VersionPattern = '^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.]+)?$'

function Write-Deploy([string]$Message, [string]$Color = 'Cyan') { Write-Host "[deploy] $Message" -ForegroundColor $Color }

# Switches this process to one environment (dev | staging | prod): compose project, env file (COMPOSE_ENV_FILES), release history
# folder. Call it first in deploy.ps1 / rollback.ps1, then Set-ComposeFiles.
function Initialize-DeployEnvironment([string]$Name) {
    $previous = Get-ActiveEnvironmentName
    if ($previous -cne 'dev' -and $env:COMPOSE_PROJECT_NAME -and $env:COMPOSE_PROJECT_NAME -ceq (Get-EnvironmentDefinition $previous).Project) {
        Remove-Item Env:COMPOSE_PROJECT_NAME -ErrorAction SilentlyContinue   # left over from an earlier staging/prod run in this shell
    }
    $def = Use-StockEnvironment $Name
    $script:EnvDef = $def
    $script:StateDir = if ($env:DEPLOY_STATE_DIR) { $env:DEPLOY_STATE_DIR }
                       elseif ($def.StateSubDir) { Join-Path $script:BaseStateDir $def.StateSubDir }
                       else { $script:BaseStateDir }
    $script:HistoryFile = Join-Path $script:StateDir 'releases.json'
    $script:ComposeProject = if ($def.Name -ceq 'dev') { $(if ($env:COMPOSE_PROJECT_NAME) { $env:COMPOSE_PROJECT_NAME } else { 'stockaccount' }) } else { $def.Project }
    return $def
}

# Compose files of the active environment (+ $Extra = drill overrides for THIS deploy only) are merged for EVERY compose call
# (deploy, rollback, backup helpers). staging/prod: docker-compose.prod.yml on top, ui-nuxt is not part of the release and
# readiness is checked inside the compose network (no api host port).
function Set-ComposeFiles([string[]]$Extra = @()) {
    $script:ProdMode = [bool]$script:EnvDef.Production
    $files = @(Get-EnvironmentComposeFilePaths $script:EnvDef.Name) + @($Extra)
    foreach ($f in $files) { if (-not (Test-Path -LiteralPath $f)) { throw "compose file not found: $f" } }
    if ($files.Count -gt 1) { $env:COMPOSE_FILE = $files -join [IO.Path]::PathSeparator }
    else { Remove-Item Env:COMPOSE_FILE -ErrorAction SilentlyContinue }
    if ($script:ProdMode) {
        $script:AppImages   = @('stockaccount-api', 'stockaccount-ui-vue')
        $script:AppServices = @('api', 'ui-vue')
    }
}

# Value of a setting: process environment first (compose gives it precedence too), then the active environment's env file
# (.env, or .env.staging for staging). $null when empty.
function Get-EnvSetting([string]$Name) {
    $fromEnv = [Environment]::GetEnvironmentVariable($Name)
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) { return $fromEnv }
    return (Read-EnvFileValue (Get-ActiveEnvFilePath) $Name)
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
    # Remember the environment (state folders are per environment anyway) for diagnostics.
    $History | Add-Member -NotePropertyName production -NotePropertyValue $script:ProdMode -Force
    $History | Add-Member -NotePropertyName environment -NotePropertyValue $script:EnvDef.Name -Force
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

# staging/prod share the local image names (stockaccount-api:<version> ...) with the dev stack. Never (re)build or retag a version
# tag that a container of the dev project (default compose project "stockaccount") is running from.
function Assert-VersionNotUsedByDev([string]$Version) {
    if ($script:EnvDef.Name -ceq 'dev') { return }
    $out = Invoke-Docker @('ps', '-a', '--filter', 'label=com.docker.compose.project=stockaccount', '--format', '{{.Names}}|{{.Image}}') -AllowFailure -Quiet
    foreach ($line in $out) {
        $parts = "$line".Split('|')
        if ($parts.Count -ge 2 -and $parts[1] -cmatch ('^stockaccount-[a-z-]+:' + [regex]::Escape($Version) + '$')) {
            throw "Version $Version is the image tag of the running dev stack ($($parts[0]) uses $($parts[1])): choose another version for $($script:EnvDef.Name) so the dev images are never rebuilt."
        }
    }
}
