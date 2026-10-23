<#
.SYNOPSIS
  Uploads new backups (.bak + .json manifest) to an S3-compatible bucket with rclone, verifies every upload
  (size + checksum) and applies the remote retention. Exit code 0 only if everything succeeded.

.DESCRIPTION
  rclone runs in a pinned container (rclone/rclone), so the host needs nothing but Docker. Settings come from the
  environment or .env (see .env.example): OFFSITE_S3_ENDPOINT (empty = AWS S3), OFFSITE_S3_REGION, OFFSITE_S3_BUCKET,
  OFFSITE_S3_ACCESS_KEY, OFFSITE_S3_SECRET_KEY, OFFSITE_PREFIX (default "stockaccount"), OFFSITE_RETENTION_DAYS
  (default 30, 0 = keep forever), OFFSITE_MIN_KEEP (default 3: the newest backups are never pruned). The keys reach
  the container only as RCLONE_CONFIG_OFFSITE_* environment variables: no config file, nothing on a command line,
  nothing in the log.
  Only backups whose manifest exists are uploaded (the manifest is written last by backup.ps1 / the sidecar);
  the .bak goes first and the manifest last, so a manifest in the bucket always means a complete backup.
  OFFSITE_ENABLED is the switch for the automatic hooks (backup.ps1, sidecar); running this script by hand works
  as soon as the bucket settings are present.
  Exit codes: 0 ok, 1 upload/verify/retention failed, 2 not configured / invalid settings.

.EXAMPLE
  ./scripts/db/offsite-sync.ps1
.EXAMPLE
  ./scripts/db/offsite-sync.ps1 -List        # show the backups in the bucket
#>
[CmdletBinding()]
param(
    [string]$BackupDir,
    [switch]$List,
    [string]$LogFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$exitCode = 1
$transcript = $false
try {
    if ($LogFile) { Start-Transcript -LiteralPath $LogFile -Append | Out-Null; $transcript = $true }
    try { $cfg = Get-OffsiteConfig }
    catch { Write-Host "[offsite] $($_.Exception.Message)" -ForegroundColor Red; $exitCode = 2; throw [OperationCanceledException]::new() }
    if (-not $BackupDir) { $BackupDir = Get-EnvironmentBackupDir (Get-ActiveEnvironmentName) }
    Assert-BackupDirIsolated (Get-ActiveEnvironmentName) $BackupDir
    if (-not (Test-Path -LiteralPath $BackupDir)) { throw "Backup folder not found: $BackupDir" }

    $mode = if ($List) { 'list' } else { 'once' }
    $engine = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'offsite-sync.sh')).Replace("`r`n", "`n")
    $r = Invoke-OffsiteContainer -Config $cfg -HostDir $BackupDir -ReadOnlyData -Entrypoint 'sh' -ContainerArgs @('-s', '--', $mode) -StdinText $engine
    Write-OffsiteLines $cfg $r.Lines
    if ($r.ExitCode -eq 0) { $exitCode = 0 }
    elseif ($r.ExitCode -eq 2) { $exitCode = 2 }
    else { $exitCode = 1 }
}
catch [OperationCanceledException] { }
catch {
    Write-Host "[offsite] FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($transcript) { Stop-Transcript | Out-Null }
}
exit $exitCode
