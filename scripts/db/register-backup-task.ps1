<#
.SYNOPSIS
  Registers (or removes) a daily Windows Task Scheduler job that runs scripts/db/backup.ps1.

.DESCRIPTION
  Default: runs as the current user, only while that user is logged on (no admin rights needed).
  -RunWhetherLoggedOn uses an S4U logon (runs without an interactive session, no stored password) and needs an
  elevated prompt. For -Target docker the user must be able to run docker (docker-users group).
  Each run appends to backups\logs\backup-task-<target>.log (transcript) and keeps -RetentionCount "scheduled"
  backups. SQL Server Express has no SQL Agent, which is why this uses Task Scheduler.
  -Offsite adds the off-site upload (offsite-sync.ps1) to every run; the task then fails (exit 5) if the upload fails.
  Linux (production VPS, PowerShell 7): the same script creates a systemd timer (default) or a cron entry:
  -Scheduler systemd|cron, -Install writes it (systemd needs root; without -Install the unit/cron text is only printed).
  Alternative on compose hosts: the sidecar in scripts/db/docker-compose.backup.yml.

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
    [switch]$Offsite,
    [ValidateSet('systemd', 'cron')][string]$Scheduler = 'systemd',
    [switch]$Install,
    [switch]$RunNow,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
if (-not $TaskName) { $TaskName = "StockAccountDb-Backup-$Target" }
$taskPath = '\StockAccount\'

# ---------------------------------------------------------------------------------------------------------------
# Linux (production VPS): PowerShell 7 + Docker. systemd timer (default) or a cron entry. Without -Install the
# files/lines are only printed. Root installs system-wide units; cron installs into the current user's crontab.
# ---------------------------------------------------------------------------------------------------------------
$onLinux = ($PSVersionTable.PSEdition -eq 'Core') -and [bool](Get-Variable -Name IsLinux -ValueOnly -ErrorAction SilentlyContinue)
if ($onLinux) {
    try {
        $repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $backupScript = Join-Path $PSScriptRoot 'backup.ps1'
        $logDir = Join-Path (Join-Path $repoRoot 'backups') 'logs'
        $logFile = Join-Path $logDir "backup-task-$Target.log"
        $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
        $unit = "stockaccount-db-backup-$Target"
        $isRoot = ((& id -u) -eq '0')
        $runUser = if ($isRoot -and $env:SUDO_USER) { $env:SUDO_USER } else { (& id -un) }
        foreach ($v in $repoRoot, $pwsh, $logFile) { if ($v -cmatch '[\s"''$`\;|&<>]') { throw "Path '$v' contains characters that are not allowed in a scheduled command (spaces/shell characters)." } }
        $cmd = "$pwsh -NoProfile -NonInteractive -File $backupScript -Target $Target -Label scheduled -RetentionCount $RetentionCount" + $(if ($Offsite) { ' -Offsite' }) + " -LogFile $logFile"
        $hh, $mm = $At.Split(':')

        if ($Scheduler -eq 'systemd') {
            $svcPath = "/etc/systemd/system/$unit.service"; $timerPath = "/etc/systemd/system/$unit.timer"
            $svc = @"
[Unit]
Description=StockAccountDb backup ($Target)$(if ($Offsite) { ' + off-site copy' })
After=docker.service
Wants=docker.service

[Service]
Type=oneshot
User=$runUser
WorkingDirectory=$repoRoot
ExecStart=$cmd
TimeoutStartSec=2h
"@
            $timer = @"
[Unit]
Description=Daily StockAccountDb backup ($Target)

[Timer]
OnCalendar=*-*-* ${hh}:${mm}:00
Persistent=true
RandomizedDelaySec=120

[Install]
WantedBy=timers.target
"@
            if ($Unregister) {
                if (-not $isRoot) { Write-Host "[db] Run as root: systemctl disable --now $unit.timer; rm $svcPath $timerPath; systemctl daemon-reload"; exit 0 }
                & systemctl disable --now "$unit.timer" 2>$null
                Remove-Item -LiteralPath $svcPath, $timerPath -ErrorAction SilentlyContinue
                & systemctl daemon-reload
                Write-Host "[db] Removed $unit.timer / $unit.service"; exit 0
            }
            if (-not $Install -or -not $isRoot) {
                Write-Host "# --- $svcPath ---`n$svc`n`n# --- $timerPath ---`n$timer`n"
                Write-Host "[db] Install as root (sudo pwsh scripts/db/register-backup-task.ps1 -Target $Target -At $At$(if ($Offsite) { ' -Offsite' }) -Install) or copy the two files above, then:"
                Write-Host "       systemctl daemon-reload && systemctl enable --now $unit.timer   # check: systemctl list-timers $unit.timer; journalctl -u $unit.service"
                if ($Install -and -not $isRoot) { Write-Host '[db] -Install needs root for systemd; nothing was written.' -ForegroundColor Yellow; exit 1 }
                exit 0
            }
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
            & chown "${runUser}:" $logDir 2>$null
            [IO.File]::WriteAllText($svcPath, $svc + "`n"); [IO.File]::WriteAllText($timerPath, $timer + "`n")
            & systemctl daemon-reload
            & systemctl enable --now "$unit.timer"
            if ($LASTEXITCODE -ne 0) { throw 'systemctl enable failed.' }
            Write-Host "[db] Installed $unit.timer (daily $At, user ${runUser}). Log: $logFile; journal: journalctl -u $unit.service"
            if ($RunNow) { & systemctl start "$unit.service"; Write-Host "[db] Started; follow with: journalctl -fu $unit.service" }
            exit 0
        }
        else {
            $marker = "# $unit"
            $line = "$mm $hh * * * cd $repoRoot && $cmd >> $logFile.out 2>&1 $marker"
            $existing = @(); if ($Install -or $Unregister) { $existing = @(& crontab -l 2>$null | Where-Object { $_ -and -not $_.EndsWith($marker) }) }
            if ($Unregister) {
                ($existing -join "`n") + "`n" | & crontab -
                Write-Host "[db] Removed cron entry $unit"; exit 0
            }
            if (-not $Install) {
                Write-Host "# add with 'crontab -e' (or rerun with -Install):`n$line"
                exit 0
            }
            New-Item -ItemType Directory -Force -Path $logDir | Out-Null
            (($existing + $line) -join "`n") + "`n" | & crontab -
            if ($LASTEXITCODE -ne 0) { throw 'crontab install failed.' }
            Write-Host "[db] Installed cron entry for ${runUser}: daily $At. Log: $logFile"
            if ($RunNow) { Write-Host '[db] Starting one run now'; & $pwsh -NoProfile -NonInteractive -File $backupScript -Target $Target -Label scheduled -RetentionCount $RetentionCount $(if ($Offsite) { '-Offsite' }) -LogFile $logFile }
            exit 0
        }
    }
    catch {
        Write-Host "[db] register-backup-task failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

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
    $arguments = ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Target {1} -Label scheduled -RetentionCount {2} -LogFile "{3}"' -f $backupScript, $Target, $RetentionCount, $logFile) + $(if ($Offsite) { ' -Offsite' })

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
