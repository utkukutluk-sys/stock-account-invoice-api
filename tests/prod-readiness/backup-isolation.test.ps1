<#
.SYNOPSIS
  Test for the backup isolation guard (scripts/_environments.ps1 + Invoke-BackupRetention in scripts/db/_common.ps1).
  Needs no Docker and never touches the real ./backups: everything runs on a throw-away tree (STOCKACCOUNT_ENV_ROOT).
  Runs under Windows PowerShell 5.1 and PowerShell 7:  pwsh -File tests/prod-readiness/backup-isolation.test.ps1
  Exit code 0 = all assertions passed.
#>
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('sa-isolation-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path (Join-Path $root 'backups/staging'), (Join-Path $root 'backups/prod'), (Join-Path $root 'backups/local')
$env:STOCKACCOUNT_ENV_ROOT = $root
Remove-Item Env:BACKUP_DIR -ErrorAction SilentlyContinue
$repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
. (Join-Path $repo 'scripts/db/_common.ps1')

$script:pass = 0; $script:fail = 0
function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { $script:pass++; Write-Host "PASS | $Name" } else { $script:fail++; Write-Host "FAIL | $Name" -ForegroundColor Red }
}
function Throws([scriptblock]$Block) { try { & $Block | Out-Null; return $false } catch { return $true } }
function Seed([string]$Dir, [string]$Label, [int]$Count) {
    for ($i = 1; $i -le $Count; $i++) {
        $stamp = '20260101-0000{0:00}' -f $i
        foreach ($ext in 'bak', 'json') { Set-Content -LiteralPath (Join-Path $Dir "StockAccountDb_docker_${stamp}Z_$Label.$ext") -Value 'x' }
    }
}
function Count([string]$Dir) { return @(Get-ChildItem -LiteralPath $Dir -File).Count }

try {
    $b = Join-Path $root 'backups'
    # ---- default directories ----
    Check 'dev default dir = backups' ((Get-EnvironmentBackupDir 'dev' 'docker') -ceq $b)
    Check 'dev local dir = backups/local' ((Get-EnvironmentBackupDir 'dev' 'local') -ceq (Join-Path $b 'local'))
    Check 'staging default dir = backups/staging' ((Get-EnvironmentBackupDir 'staging') -ceq (Join-Path $b 'staging'))
    Check 'prod default dir = backups/prod' ((Get-EnvironmentBackupDir 'prod') -ceq (Join-Path $b 'prod'))
    $env:BACKUP_DIR = (Join-Path $root 'srv-backups')
    Check 'prod honours BACKUP_DIR' ((Get-EnvironmentBackupDir 'prod') -ceq (Join-Path $root 'srv-backups'))
    Remove-Item Env:BACKUP_DIR

    # ---- the guard ----
    Check 'staging may use backups/staging' (-not (Throws { Assert-BackupDirIsolated 'staging' (Join-Path $b 'staging') }))
    Check 'staging may use a sub folder of backups/staging' (-not (Throws { Assert-BackupDirIsolated 'staging' (Join-Path $b 'staging/x') }))
    Check 'staging REFUSES the dev dir (backups)' (Throws { Assert-BackupDirIsolated 'staging' $b })
    Check 'staging REFUSES backups/local' (Throws { Assert-BackupDirIsolated 'staging' (Join-Path $b 'local') })
    Check 'staging REFUSES backups/prod' (Throws { Assert-BackupDirIsolated 'staging' (Join-Path $b 'prod') })
    Check 'staging REFUSES a ../ escape' (Throws { Assert-BackupDirIsolated 'staging' (Join-Path $b 'staging/../') })
    Check 'staging REFUSES a look-alike sibling (staging-old)' (Throws { Assert-BackupDirIsolated 'staging' (Join-Path $b 'staging-old') })
    Check 'prod REFUSES the dev dir (backups)' (Throws { Assert-BackupDirIsolated 'prod' $b })
    Check 'prod REFUSES backups/staging' (Throws { Assert-BackupDirIsolated 'prod' (Join-Path $b 'staging') })
    Check 'prod may use backups/prod' (-not (Throws { Assert-BackupDirIsolated 'prod' (Join-Path $b 'prod') }))
    Check 'prod may use another configured dir' (-not (Throws { Assert-BackupDirIsolated 'prod' (Join-Path $root 'srv-backups') }))
    Check 'dev may use backups and backups/local' (-not (Throws { Assert-BackupDirIsolated 'dev' $b; Assert-BackupDirIsolated 'dev' (Join-Path $b 'local') }))
    Check 'dev REFUSES backups/staging' (Throws { Assert-BackupDirIsolated 'dev' (Join-Path $b 'staging') })
    Check 'dev REFUSES backups/prod' (Throws { Assert-BackupDirIsolated 'dev' (Join-Path $b 'prod') })

    # ---- Get-BackupLocation uses the active environment ----
    $ctx = [pscustomobject]@{ Target = 'local'; Runner = 'x' }
    $env:STOCKACCOUNT_ENV = 'staging'
    Check 'Get-BackupLocation (staging) REFUSES an explicit dev dir' (Throws { Get-BackupLocation -Ctx $ctx -BackupDir $b })
    $env:STOCKACCOUNT_ENV = 'dev'
    Check 'Get-BackupLocation (dev) REFUSES an explicit staging dir' (Throws { Get-BackupLocation -Ctx $ctx -BackupDir (Join-Path $b 'staging') })
    Remove-Item Env:STOCKACCOUNT_ENV

    # ---- retention never leaves the environment's directory ----
    Seed $b 'pre-deploy-1.0.0' 6                         # 6 dev backups (12 files) in backups/
    Seed (Join-Path $b 'staging') 'pre-deploy-1.0.0' 6  # 6 staging backups
    Seed (Join-Path $b 'prod') 'pre-deploy-1.0.0' 6
    $devBefore = Count $b
    $removed = @(Invoke-BackupRetention -HostDir (Join-Path $b 'staging') -Database 'StockAccountDb' -Target 'docker' -Family 'pre-deploy' -Keep 2 -Environment 'staging')
    Check 'staging retention removed 4 backups in backups/staging' ($removed.Count -eq 4 -and (Count (Join-Path $b 'staging')) -eq 4)
    Check 'staging retention left every dev file alone' ((Count $b) -eq $devBefore)
    Check 'staging retention left prod files alone' ((Count (Join-Path $b 'prod')) -eq 12)
    $devBefore = Count $b
    Check 'staging retention pointed at the dev dir is REFUSED' (Throws { Invoke-BackupRetention -HostDir $b -Database 'StockAccountDb' -Target 'docker' -Family 'pre-deploy' -Keep 1 -Environment 'staging' })
    Check 'prod retention pointed at the dev dir is REFUSED' (Throws { Invoke-BackupRetention -HostDir $b -Database 'StockAccountDb' -Target 'docker' -Family 'pre-deploy' -Keep 1 -Environment 'prod' })
    Check 'refused retention deleted nothing' ((Count $b) -eq $devBefore)
    $null = Invoke-BackupRetention -HostDir $b -Database 'StockAccountDb' -Target 'docker' -Family 'pre-deploy' -Keep 3 -Environment 'dev'
    Check 'dev retention prunes only backups/ (6 -> 3 backups, subfolders untouched)' ((Get-ChildItem -LiteralPath $b -File).Count -eq 6 -and (Count (Join-Path $b 'staging')) -eq 4 -and (Count (Join-Path $b 'prod')) -eq 12)
}
finally {
    Remove-Item Env:STOCKACCOUNT_ENV_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:STOCKACCOUNT_ENV -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ("RESULT pass={0} fail={1}" -f $script:pass, $script:fail)
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
