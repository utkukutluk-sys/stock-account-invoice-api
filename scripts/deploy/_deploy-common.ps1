# Shared helpers for scripts/deploy/*.ps1 (dot-sourced). Windows PowerShell 5.1 and PowerShell 7 compatible.
Set-StrictMode -Version 3.0

$script:RepoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:StateDir    = Join-Path $PSScriptRoot '.state'
$script:HistoryFile = Join-Path $script:StateDir 'releases.json'
$script:AppImages   = @('stockaccount-api', 'stockaccount-ui-vue', 'stockaccount-ui-nuxt')
$script:AppServices = @('api', 'ui-vue', 'ui-nuxt')
$script:VersionPattern = '^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.]+)?$'

function Write-Deploy([string]$Message, [string]$Color = 'Cyan') { Write-Host "[deploy] $Message" -ForegroundColor $Color }

function Get-DockerExe {
    $candidates = @()
    if ($env:DOCKER_EXE) { $candidates += $env:DOCKER_EXE }
    $cmd = Get-Command docker -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe')
    $candidates += (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe')
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
    $json = $History | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($script:HistoryFile, $json, (New-Object Text.UTF8Encoding($false)))
}

function Add-HistoryEvent($History, [hashtable]$Event) {
    $Event['atUtc'] = (Get-Date).ToUniversalTime().ToString('o')
    $History.releases = @($History.releases) + @([pscustomobject]$Event)
}

# Version that was live immediately before $Current (last successful deploy/rollback to a different version).
function Get-PreviousVersion($History, [string]$Current) {
    $live = @($History.releases | Where-Object { $_.status -in @('deployed', 'rolled-back-to') } | ForEach-Object { $_.version })
    for ($i = $live.Count - 1; $i -ge 0; $i--) {
        if ($live[$i] -ne $Current) { return $live[$i] }
    }
    return $null
}

function Test-ImageExists([string]$Image) {
    $out = Invoke-Docker @('image', 'inspect', '--format', '{{.Id}}', $Image) -AllowFailure -Quiet
    return ($script:LastDockerExit -eq 0 -and ($out -join "") -match "^sha256:")
}

function Test-ServiceRunning([string]$Service) {
    $id = (Invoke-Docker @('compose', 'ps', '-q', '--status', 'running', $Service) -AllowFailure -Quiet) -join ''
    return -not [string]::IsNullOrWhiteSpace($id)
}

# Polls the readiness endpoint until 200 and the api container runs the expected image tag.
function Wait-Ready([string]$Version, [string]$HealthUrl, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $expectedImage = "stockaccount-api:$Version"
    while ((Get-Date) -lt $deadline) {
        $cid = ((Invoke-Docker @('compose', 'ps', '-q', 'api') -AllowFailure -Quiet) -join '').Trim()
        if ($cid) {
            $image = ((Invoke-Docker @('inspect', '--format', '{{.Config.Image}}', $cid) -AllowFailure -Quiet) -join '').Trim()
            if ($image -eq $expectedImage) {
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
