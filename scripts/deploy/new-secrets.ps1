<#
.SYNOPSIS
  Writes strong random secrets into .env (JWT_KEY, MSSQL_SA_PASSWORD, APP_DB_PASSWORD, BOOTSTRAP_ADMIN_PASSWORD) and keeps every other line.

.DESCRIPTION
  - JWT_KEY: 64 random bytes, Base64Url (86 chars).
  - MSSQL_SA_PASSWORD, APP_DB_PASSWORD: 28 chars with upper, lower, digits and symbols (SQL Server policy).
    APP_DB_PASSWORD is the least-privilege runtime login; db-migrate creates it / syncs it (ALTER LOGIN) on every "up".
  - BOOTSTRAP_ADMIN_PASSWORD: password of the first Admin created by the production deploy (db-migrate bootstrap). It is the
    ONE value you need to read from .env (log in, change it in the UI, then delete the line from .env).
  - -Environment staging writes .env.staging (DOMAIN=localhost, LOCAL_TLS=internal, BACKUP_DIR=./backups/staging, default admin staging-admin@stockaccount.local); dev/prod write .env.
  - -AdminEmail sets BOOTSTRAP_ADMIN_EMAIL (asked interactively when missing); -Domain / -AcmeEmail set DOMAIN / ACME_EMAIL.
  Existing non-placeholder values are kept unless -Force. Secret values are never printed.
  NOTE: the SA password is applied by SQL Server only when the mssql data volume is created. Changing it for an existing
  volume requires `docker compose down -v` (data loss: take a backup first) or ALTER LOGIN sa inside the container.

.EXAMPLE
  ./scripts/deploy/new-secrets.ps1
.EXAMPLE
  pwsh ./scripts/deploy/new-secrets.ps1 -AdminEmail owner@example.com -Domain app.example.com -AcmeEmail owner@example.com
#>
[CmdletBinding()]
param(
    [switch]$Force,
    # Which secrets to (re)generate. Use -Keys JWT_KEY for an existing mssql volume (the SA password is fixed at volume creation).
    [ValidateSet('JWT_KEY', 'MSSQL_SA_PASSWORD', 'APP_DB_PASSWORD', 'BOOTSTRAP_ADMIN_PASSWORD')][string[]]$Keys = @('JWT_KEY', 'MSSQL_SA_PASSWORD', 'APP_DB_PASSWORD', 'BOOTSTRAP_ADMIN_PASSWORD'),
    # First Admin of a production deployment (BOOTSTRAP_ADMIN_EMAIL). Prompted when missing and the session is interactive.
    [string]$AdminEmail,
    # Production host name (DOMAIN) and Lets Encrypt e-mail (ACME_EMAIL).
    [string]$Domain,
    [string]$AcmeEmail,
    # dev/prod (default): .env. staging: .env.staging with DOMAIN=localhost, LOCAL_TLS=internal, a bootstrap admin and BACKUP_DIR=./backups/staging.
    [ValidateSet('dev', 'staging', 'prod')][string]$Environment = 'dev'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$envFile = Join-Path $repo $(if ($Environment -ceq 'staging') { '.env.staging' } else { '.env' })
$example = Join-Path $repo '.env.example'

function New-RandomBytes([int]$Count) {
    $bytes = New-Object byte[] $Count
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return $bytes
}

function New-JwtKey { return [Convert]::ToBase64String((New-RandomBytes 64)).TrimEnd('=').Replace('+', '-').Replace('/', '_') }

function New-SaPassword {
    $sets = @('ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!+-_.@')
    $all = -join $sets
    $chars = New-Object System.Collections.Generic.List[char]
    foreach ($s in $sets) { $chars.Add($s[(New-RandomBytes 1)[0] % $s.Length]) }
    foreach ($b in (New-RandomBytes 24)) { $chars.Add($all[$b % $all.Length]) }
    # shuffle
    $shuffled = $chars | Sort-Object { (New-RandomBytes 4 | ForEach-Object { $_ }) -join '' }
    return -join $shuffled
}

function Test-Placeholder([string]$Key, [string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return $true }
    # Ordinal / culture-invariant comparisons only (tr-TR: -match/-eq are culture-sensitive for 'I'/'i').
    if ($Key -ceq 'JWT_KEY') { return [regex]::IsMatch($Value, 'change-me|dev-only', [Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant') }
    if ($Key -ceq 'MSSQL_SA_PASSWORD') { return ($Value -ceq 'StockAccount!2026') }
    if ($Key -ceq 'APP_DB_PASSWORD') { return ($Value -ceq 'App!Change-Me-2026') }
    if ($Key -ceq 'BOOTSTRAP_ADMIN_PASSWORD') { return $false } # only empty (handled above) counts as missing
    return $false
}

$lines = if (Test-Path -LiteralPath $envFile) { [IO.File]::ReadAllLines($envFile) } else { [IO.File]::ReadAllLines($example) }
$generated = @{ JWT_KEY = (New-JwtKey); MSSQL_SA_PASSWORD = (New-SaPassword); APP_DB_PASSWORD = (New-SaPassword); BOOTSTRAP_ADMIN_PASSWORD = (New-SaPassword) }

# Plain settings (not secrets): written when a parameter was given, or asked for (admin e-mail) when missing.
$plain = [ordered]@{}
if ($AdminEmail) { $plain['BOOTSTRAP_ADMIN_EMAIL'] = $AdminEmail.Trim() }
if ($Domain) { $plain['DOMAIN'] = $Domain.Trim() }
if ($AcmeEmail) { $plain['ACME_EMAIL'] = $AcmeEmail.Trim() }
if ($Environment -ceq 'staging') {
    # Staging is a local, production-like stack: Caddy local CA on localhost, admin created by the bootstrap step, own backup folder.
    if (-not $plain.Contains('DOMAIN')) { $plain['DOMAIN'] = 'localhost' }
    $plain['LOCAL_TLS'] = 'internal'
    $plain['BACKUP_DIR'] = './backups/staging'
    if (-not $plain.Contains('BOOTSTRAP_ADMIN_EMAIL') -and -not ($lines | Where-Object { $_ -cmatch '^\s*BOOTSTRAP_ADMIN_EMAIL\s*=\s*\S' })) { $plain['BOOTSTRAP_ADMIN_EMAIL'] = 'staging-admin@stockaccount.local' }
}
if (-not $plain.Contains('BOOTSTRAP_ADMIN_EMAIL')) {
    $existingEmail = $lines | Where-Object { $_ -cmatch '^\s*BOOTSTRAP_ADMIN_EMAIL\s*=\s*\S' } | Select-Object -First 1
    if (-not $existingEmail -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        $answer = Read-Host 'Admin e-mail for the first production login (empty = skip, demo seed is used in dev)'
        if ($answer) { $plain['BOOTSTRAP_ADMIN_EMAIL'] = $answer.Trim() }
    }
}
foreach ($v in $plain.Values) { if ($v -cmatch '[\s#"''$]') { throw "Value '$v' contains characters that are not allowed in .env (space, #, quotes, `$)." } }

$seen = @{}
$out = foreach ($line in $lines) {
    if ($line -cmatch '^\s*(BOOTSTRAP_ADMIN_EMAIL|DOMAIN|ACME_EMAIL|LOCAL_TLS|BACKUP_DIR)\s*=') {
        $key = $Matches[1]
        $seen[$key] = $true
        if ($plain.Contains($key)) { Write-Host "[secrets] $key set"; "$key=$($plain[$key])" } else { $line }
    }
    elseif ($line -cmatch '^\s*(JWT_KEY|MSSQL_SA_PASSWORD|APP_DB_PASSWORD|BOOTSTRAP_ADMIN_PASSWORD)\s*=\s*(.*)$') {
        $key = $Matches[1]; $value = $Matches[2].Trim()
        $seen[$key] = $true
        if (($Keys -contains $key) -and ($Force -or (Test-Placeholder $key $value))) {
            Write-Host "[secrets] $key generated"
            "$key=$($generated[$key])"
        }
        else {
            Write-Host "[secrets] $key kept (real value or not selected; use -Force / -Keys to replace)"
            $line
        }
    }
    else { $line }
}
foreach ($key in $plain.Keys) {
    if (-not $seen.ContainsKey($key)) { Write-Host "[secrets] $key set"; $out += "$key=$($plain[$key])" }
}
foreach ($key in $Keys) {
    if (-not $seen.ContainsKey($key)) { Write-Host "[secrets] $key generated"; $out += "$key=$($generated[$key])" }
}

[IO.File]::WriteAllLines($envFile, [string[]]$out, (New-Object Text.UTF8Encoding($false)))
Write-Host "[secrets] wrote $envFile (values not shown). If the mssql volume already exists, see the note in this script about the SA password."
