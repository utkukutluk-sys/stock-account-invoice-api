<#
.SYNOPSIS
  Writes strong random secrets into .env (JWT_KEY, MSSQL_SA_PASSWORD) and keeps every other line.

.DESCRIPTION
  - JWT_KEY: 64 random bytes, Base64Url (86 chars).
  - MSSQL_SA_PASSWORD, APP_DB_PASSWORD: 28 chars with upper, lower, digits and symbols (SQL Server policy).
    APP_DB_PASSWORD is the least-privilege runtime login; db-migrate creates it / syncs it (ALTER LOGIN) on every "up".
  Existing non-placeholder values are kept unless -Force. Values are never printed.
  NOTE: the SA password is applied by SQL Server only when the mssql data volume is created. Changing it for an existing
  volume requires `docker compose down -v` (data loss: take a backup first) or ALTER LOGIN sa inside the container.

.EXAMPLE
  ./scripts/deploy/new-secrets.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force,
    # Which secrets to (re)generate. Use -Keys JWT_KEY for an existing mssql volume (the SA password is fixed at volume creation).
    [ValidateSet('JWT_KEY', 'MSSQL_SA_PASSWORD', 'APP_DB_PASSWORD')][string[]]$Keys = @('JWT_KEY', 'MSSQL_SA_PASSWORD', 'APP_DB_PASSWORD')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$envFile = Join-Path $repo '.env'
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
    return $false
}

$lines = if (Test-Path -LiteralPath $envFile) { [IO.File]::ReadAllLines($envFile) } else { [IO.File]::ReadAllLines($example) }
$generated = @{ JWT_KEY = (New-JwtKey); MSSQL_SA_PASSWORD = (New-SaPassword); APP_DB_PASSWORD = (New-SaPassword) }
$seen = @{}
$out = foreach ($line in $lines) {
    if ($line -cmatch '^\s*(JWT_KEY|MSSQL_SA_PASSWORD|APP_DB_PASSWORD)\s*=\s*(.*)$') {
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
foreach ($key in $Keys) {
    if (-not $seen.ContainsKey($key)) { Write-Host "[secrets] $key generated"; $out += "$key=$($generated[$key])" }
}

[IO.File]::WriteAllLines($envFile, [string[]]$out, (New-Object Text.UTF8Encoding($false)))
Write-Host "[secrets] wrote $envFile (values not shown). If the mssql volume already exists, see the note in this script about the SA password."
