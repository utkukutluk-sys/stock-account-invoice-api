<#
.SYNOPSIS
  Prints the current EF Core migration head (latest MigrationId in __EFMigrationsHistory).

.DESCRIPTION
  Used by scripts/deploy to record the schema version before and after a deploy.
  stdout: only the MigrationId (or JSON with -Json). Exit codes: 0 found, 2 database or history table missing,
  1 error.

.EXAMPLE
  $head = ./scripts/db/migration-head.ps1 -Target docker
#>
[CmdletBinding()]
param(
    [ValidateSet('local', 'docker')][string]$Target = 'local',
    [string]$Database = 'StockAccountDb',
    [string]$ServerInstance = '.\SQLEXPRESS',
    [string]$ComposeFile,
    [string]$Service = 'mssql',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

try {
    $ctx = New-DbContext -Target $Target -Database $Database -ServerInstance $ServerInstance -ComposeFile $ComposeFile -Service $Service
    $s = Get-DbState $ctx
    $found = $s.Exists -and $s.State -eq 'ONLINE' -and $s.MigrationHead
    if ($Json) {
        [ordered]@{ database = $Database; target = $Target; exists = $s.Exists; state = $s.State; migrationHead = $s.MigrationHead; migrationCount = $s.MigrationCount } | ConvertTo-Json -Compress
    }
    elseif ($found) {
        $s.MigrationHead
    }
    if (-not $found) {
        [Console]::Error.WriteLine("No migration head: database '$Database' exists=$($s.Exists) state=$($s.State).")
        exit 2
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine("migration-head failed: $($_.Exception.Message)")
    exit 1
}
