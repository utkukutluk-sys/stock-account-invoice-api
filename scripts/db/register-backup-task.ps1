<#
.SYNOPSIS
  Registers (or removes) a daily Windows Task Scheduler job that runs scripts/db/backup.ps1.

.DESCRIPTION
  Default: runs as the current user, only while that user is logged on (no admin rights needed).
  -RunWhetherLoggedOn uses an S4U logon (runs without an interactive session, no stored password) and needs an
  elevated prompt. For -Target docker the user must be able to run docker (docker-users group).
  Each run appends to backups\logs\backup-task-<target>.log (transcript) and keeps -RetentionCount "scheduled"
  backups. SQL Server Express has no SQL Agent, which is why this uses Task Scheduler.
  For Linux/compose hosts use the sidecar in scripts/db/docker-compose.backup.yml instead.

.EXAMPLE
  ./scripts/db/register-backup-task.ps1 -Target local -At 02:30 -RetentionCount 14
.EXAMPLE
  ./scripts/db/register-backup-task.ps1 -Target docker -RunNow
.EXAMPLE
  ./scripts/db/register-backup-task.ps1 -Target local -Unregister
#>
[CmdletBinding()]
param(
    [ValidateSet('local', 'docker')][string]$Target = 'local',
    [ValidatePattern('^([01]\d|2[0-3]):[0-5]\d$')][string]$At = '02:00',
    [ValidateRange(1, 1000)][int]$RetentionCount = 14,
    [ValidatePattern('^[A-Za-z0-9 ._-]{1,100}$')][string]$TaskName,
    [switch]$RunWhetherLoggedOn,
    [switch]$RunNow,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
if (-not $TaskName) { $TaskName = "StockAccountDb-Backup-$Target" }
$taskPath = '\StockAccount\'

try {
    if ($Unregister) {
        if (Get-ScheduledTask -TaskName $TaskName -TaskPath $taskPath -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -TaskPath $taskPath -Confirm:$false
            Write-Host "[db] Removed scheduled task $taskPath$TaskName"
        }
        else { Write-Host "[db] Scheduled task $taskPath$TaskName does not exist" }
        exit 0
    }

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $backupScript = Join-Path $PSScriptRoot 'backup.ps1'
    $logDir = Join-Path $repoRoot 'backups\logs'
    if (-not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
    $logFile = Join-Path $logDir "backup-task-$Target.log"

    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Target {1} -Label scheduled -RetentionCount {2} -LogFile "{3}"' -f $backupScript, $Target, $RetentionCount, $logFile)

    $action = New-ScheduledTaskAction -Execute $psExe -Argument $arguments -WorkingDirectory $repoRoot
    $trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::ParseExact($At, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture))
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 10)
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $logonType = if ($RunWhetherLoggedOn) { 'S4U' } else { 'Interactive' }
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType $logonType -RunLevel Limited

    Register-ScheduledTask -TaskName $TaskName -TaskPath $taskPath -Action $action -Trigger $trigger -Settings $settings `
        -Principal $principal -Description "StockAccountDb full backup ($Target), keeps $RetentionCount scheduled backups. Log: $logFile" -Force | Out-Null
    Write-Host "[db] Registered $taskPath$TaskName daily at $At as $user ($logonType). Log: $logFile"

    if ($RunNow) {
        Start-ScheduledTask -TaskName $TaskName -TaskPath $taskPath
        Write-Host '[db] Started; check the log or: Get-ScheduledTaskInfo -TaskPath \StockAccount\ -TaskName ' $TaskName
    }
    exit 0
}
catch {
    Write-Host "[db] register-backup-task failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
