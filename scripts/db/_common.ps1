# Shared helpers for scripts/db/*.ps1 (dot-sourced). Windows PowerShell 5.1 and PowerShell 7 compatible.
#
# Security rules followed here:
# - Every value that ends up inside T-SQL (database name, file names, paths, labels) is validated
#   against a strict regex first AND then emitted through ConvertTo-SqlLiteral / ConvertTo-SqlIdentifier
#   (quote doubling), never concatenated raw.
# - The SA password (docker target) is read from $env:MSSQL_SA_PASSWORD or .env, handed to sqlcmd only
#   through the SQLCMDPASSWORD environment variable of the child process and never printed.

Set-StrictMode -Version 3.0

$script:RepoRoot           = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:DbNamePattern      = '^[A-Za-z_][A-Za-z0-9_]{0,127}$'
$script:LabelPattern       = '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'
$script:BackupFilePattern  = '^[A-Za-z0-9_][A-Za-z0-9._-]{0,200}\.bak$'
$script:ServicePattern     = '^[a-z0-9][a-z0-9_.-]{0,62}$'
$script:InstancePattern    = '^[A-Za-z0-9_.\-]+(\\[A-Za-z0-9_$]+)?(,\d{1,5})?$'
$script:SafePathPattern    = '^[^''";|<>*?\r\n]+$'
$script:ContainerBackupDir = '/var/opt/mssql/backup'
$script:ContainerSqlcmd    = '/opt/mssql-tools18/bin/sqlcmd'

function Write-Step([string]$Message) { Write-Host "[db] $Message" }

function Assert-Pattern([string]$Value, [string]$Pattern, [string]$What) {
    if ([string]::IsNullOrEmpty($Value) -or $Value -cnotmatch $Pattern) {
        throw "Invalid $What '$Value' (must match $Pattern)."
    }
}

function ConvertTo-SqlLiteral([string]$Value) { return "N'" + $Value.Replace("'", "''") + "'" }
function ConvertTo-SqlIdentifier([string]$Value) { return '[' + $Value.Replace(']', ']]') + ']' }

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

function Get-SaPassword {
    if ($env:MSSQL_SA_PASSWORD) { return $env:MSSQL_SA_PASSWORD }
    $envFile = Join-Path $script:RepoRoot '.env'
    if (-not (Test-Path -LiteralPath $envFile)) { throw ".env not found at $envFile and MSSQL_SA_PASSWORD is not set." }
    foreach ($line in [IO.File]::ReadAllLines($envFile)) {
        if ($line -cmatch '^\s*MSSQL_SA_PASSWORD\s*=\s*(.*)$') {
            $value = $Matches[1].Trim()
            if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            if ($value) { return $value }
        }
    }
    throw "MSSQL_SA_PASSWORD not found in $envFile."
}

# Runs a native executable without letting PS 5.1 turn stderr into terminating errors.
function Invoke-Native([string]$Exe, [string[]]$Arguments, [string]$InputText = $null) {
    $prev = $ErrorActionPreference
    $prevEnc = $OutputEncoding
    $ErrorActionPreference = 'Continue'
    # stdin to native programs: UTF-8 without BOM (a BOM breaks sqlcmd's first statement)
    $OutputEncoding = New-Object Text.UTF8Encoding($false)
    try {
        if ($null -ne $InputText -and $InputText -ne '') { $out = $InputText | & $Exe @Arguments 2>&1 }
        else { $out = & $Exe @Arguments 2>&1 }
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev; $OutputEncoding = $prevEnc }
    $lines = @($out | ForEach-Object { "$_" })
    return [pscustomobject]@{ ExitCode = $code; Lines = $lines }
}

<#
  Creates the execution context for a target.
  local : .\SQLEXPRESS (or -ServerInstance), Windows auth; sqlcmd if installed, else Invoke-Sqlcmd.
  docker: `docker compose exec mssql /opt/mssql-tools18/bin/sqlcmd` as sa.
#>
function New-DbContext {
    param(
        [ValidateSet('local', 'docker')][string]$Target,
        [string]$Database = 'StockAccountDb',
        [string]$ServerInstance = '.\SQLEXPRESS',
        [string]$ComposeFile,
        [string]$Service = 'mssql'
    )
    Assert-Pattern $Database $script:DbNamePattern 'database name'
    $ctx = [ordered]@{ Target = $Target; Database = $Database; Runner = $null; ServerInstance = $null; Docker = $null; ComposeArgs = $null; Service = $null }
    if ($Target -eq 'local') {
        Assert-Pattern $ServerInstance $script:InstancePattern 'server instance'
        $ctx.ServerInstance = $ServerInstance
        $sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
        if ($sqlcmd) { $ctx.Runner = 'sqlcmd'; $ctx.SqlcmdPath = $sqlcmd.Source }
        elseif (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue) { $ctx.Runner = 'invoke-sqlcmd' }
        else {
            foreach ($m in 'SqlServer', 'SQLPS') {
                if (Get-Module -ListAvailable $m) { Import-Module $m -DisableNameChecking -ErrorAction Stop; $ctx.Runner = 'invoke-sqlcmd'; break }
            }
            if (-not $ctx.Runner) { throw 'Neither sqlcmd nor Invoke-Sqlcmd (SqlServer/SQLPS module) is available.' }
        }
    }
    else {
        Assert-Pattern $Service $script:ServicePattern 'compose service'
        if (-not $ComposeFile) { $ComposeFile = Join-Path $script:RepoRoot 'docker-compose.yml' }
        if (-not (Test-Path -LiteralPath $ComposeFile)) { throw "Compose file not found: $ComposeFile" }
        $ctx.Runner = 'docker'
        $ctx.Docker = Get-DockerExe
        $ctx.ComposeArgs = @('compose', '--project-directory', $script:RepoRoot, '-f', (Resolve-Path -LiteralPath $ComposeFile).Path)
        $ctx.Service = $Service
    }
    return [pscustomobject]$ctx
}

<#
  Executes a T-SQL script (may contain GO) against master and returns the output lines.
  Result sets are printed without headers, columns separated by '|'. Throws on any SQL error.
#>
function Invoke-DbSql {
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$Sql)

    switch ($Ctx.Runner) {
        'sqlcmd' {
            $tmp = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'sa-db-' + [Guid]::NewGuid().ToString('N') + '.sql')
            [IO.File]::WriteAllText($tmp, $Sql, (New-Object Text.UTF8Encoding($true)))
            try {
                $r = Invoke-Native $Ctx.SqlcmdPath @('-S', $Ctx.ServerInstance, '-E', '-C', '-d', 'master', '-b', '-I', '-h', '-1', '-W', '-s', '|', '-f', '65001', '-i', $tmp)
            }
            finally { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
        }
        'invoke-sqlcmd' {
            $tmp = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'sa-db-' + [Guid]::NewGuid().ToString('N') + '.sql')
            [IO.File]::WriteAllText($tmp, $Sql, (New-Object Text.UTF8Encoding($true)))
            try {
                $p = @{ ServerInstance = $Ctx.ServerInstance; Database = 'master'; InputFile = $tmp; QueryTimeout = 65535; ErrorAction = 'Stop'; Verbose = $true }
                if ((Get-Command Invoke-Sqlcmd).Parameters.ContainsKey('TrustServerCertificate')) { $p.TrustServerCertificate = $true }
                $rows = Invoke-Sqlcmd @p 4>&1
                $lines = foreach ($row in @($rows)) {
                    if ($row -is [Data.DataRow]) { ($row.ItemArray | ForEach-Object { "$_" }) -join '|' } else { "$row" }
                }
                $r = [pscustomobject]@{ ExitCode = 0; Lines = @($lines) }
            }
            catch { $r = [pscustomobject]@{ ExitCode = 1; Lines = @($_.Exception.Message) } }
            finally { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
        }
        'docker' {
            # The script travels base64-encoded in an environment variable (no stdin/BOM or argument-quoting
            # issues on Windows PowerShell 5.1); the password travels in SQLCMDPASSWORD. Neither is printed.
            $hadPwd = Test-Path Env:SQLCMDPASSWORD
            $oldPwd = $env:SQLCMDPASSWORD
            $env:SQLCMDPASSWORD = Get-SaPassword
            $env:SA_SQL_B64 = [Convert]::ToBase64String((New-Object Text.UTF8Encoding($false)).GetBytes($Sql + "`nGO`n"))
            try {
                $sh = 'f=$(mktemp) && printf %s $SA_SQL_B64 | base64 -d > $f && ' + $script:ContainerSqlcmd +
                ' -S localhost -U sa -C -d master -b -I -h -1 -W -s "|" -i $f; rc=$?; rm -f $f; exit $rc'
                $sh = $sh.Replace('"|"', "'|'")
                $dargs = $Ctx.ComposeArgs + @('exec', '-T', '-e', 'SQLCMDPASSWORD', '-e', 'SA_SQL_B64', $Ctx.Service, 'sh', '-c', $sh)
                $r = Invoke-Native $Ctx.Docker $dargs
            }
            finally {
                Remove-Item Env:SA_SQL_B64 -ErrorAction SilentlyContinue
                if ($hadPwd) { $env:SQLCMDPASSWORD = $oldPwd } else { Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue }
            }
        }
        default { throw "Unknown runner $($Ctx.Runner)" }
    }

    if ($r.ExitCode -ne 0) {
        $msg = ($r.Lines | Where-Object { $_ -and $_.Trim() }) -join [Environment]::NewLine
        throw "SQL failed (exit $($r.ExitCode)) on target '$($Ctx.Target)':$([Environment]::NewLine)$msg"
    }
    return @($r.Lines | Where-Object { $_ -ne $null -and $_.Trim() -ne '' } | ForEach-Object { $_.Trim() })
}

# Runs a command inside the mssql container (no SQL). Returns output lines; throws on failure.
function Invoke-InContainer {
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string[]]$Command, [switch]$AllowFailure)
    $r = Invoke-Native $Ctx.Docker ($Ctx.ComposeArgs + @('exec', '-T', $Ctx.Service) + $Command)
    if ($r.ExitCode -ne 0 -and -not $AllowFailure) { throw "Container command failed ($($Command -join ' ')): $($r.Lines -join ' ')" }
    return $r
}

function Invoke-ComposeCp {
    param([Parameter(Mandatory)]$Ctx, [Parameter(Mandatory)][string]$From, [Parameter(Mandatory)][string]$To)
    $r = Invoke-Native $Ctx.Docker ($Ctx.ComposeArgs + @('cp', $From, $To))
    if ($r.ExitCode -ne 0) { throw "docker compose cp failed: $($r.Lines -join ' ')" }
}

# Parses "KEY=value" lines emitted by the state queries.
function ConvertFrom-KeyValueLines([string[]]$Lines) {
    $h = [ordered]@{}
    foreach ($l in $Lines) {
        foreach ($seg in ($l -split '\|')) {
            if ($seg -cmatch '^([A-Za-z0-9_.]+)=(.*)$') { $h[$Matches[1]] = $Matches[2].Trim() }
        }
    }
    return $h
}

<#
  T-SQL that reports server + database state as KEY=value lines:
  DB_EXISTS, ENGINE_EDITION, EDITION, VERSION, DB_STATE, USER_ACCESS, RECOVERY, LOG_REUSE_WAIT,
  MIGRATION_HEAD, MIGRATION_COUNT, ROWS_<schema>.<table> (exact row counts from partition stats).
#>
function Get-DbStateSql([string]$Database) {
    Assert-Pattern $Database $script:DbNamePattern 'database name'
    $lit = ConvertTo-SqlLiteral $Database
    $id = ConvertTo-SqlIdentifier $Database
    return @"
SET NOCOUNT ON;
SELECT 'DB_EXISTS=' + CASE WHEN DB_ID($lit) IS NULL THEN '0' ELSE '1' END;
SELECT 'ENGINE_EDITION=' + CONVERT(varchar(10), SERVERPROPERTY('EngineEdition'));
SELECT 'EDITION=' + CONVERT(nvarchar(128), SERVERPROPERTY('Edition')) COLLATE DATABASE_DEFAULT;
SELECT 'VERSION=' + CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) COLLATE DATABASE_DEFAULT;
SELECT 'DATA_PATH=' + CONVERT(nvarchar(400), SERVERPROPERTY('InstanceDefaultDataPath')) COLLATE DATABASE_DEFAULT;
SELECT 'LOG_PATH=' + CONVERT(nvarchar(400), SERVERPROPERTY('InstanceDefaultLogPath')) COLLATE DATABASE_DEFAULT;
IF DB_ID($lit) IS NOT NULL
BEGIN
    SELECT 'DB_STATE=' + (state_desc COLLATE DATABASE_DEFAULT),
           'USER_ACCESS=' + (user_access_desc COLLATE DATABASE_DEFAULT),
           'RECOVERY=' + (recovery_model_desc COLLATE DATABASE_DEFAULT),
           'LOG_REUSE_WAIT=' + (log_reuse_wait_desc COLLATE DATABASE_DEFAULT)
    FROM sys.databases WHERE name = $lit;
    SELECT 'FILE_' + CASE type WHEN 0 THEN 'D' ELSE 'L' END + CONVERT(varchar(10), file_id) + '=' + (physical_name COLLATE DATABASE_DEFAULT)
    FROM sys.master_files WHERE database_id = DB_ID($lit) AND type IN (0, 1);
    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = $lit AND state_desc = 'ONLINE')
        EXEC $id.sys.sp_executesql N'
SET NOCOUNT ON;
IF OBJECT_ID(N''dbo.__EFMigrationsHistory'') IS NOT NULL
    SELECT ''MIGRATION_HEAD='' + ISNULL((SELECT TOP (1) MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId DESC), N'''') COLLATE DATABASE_DEFAULT,
           ''MIGRATION_COUNT='' + CONVERT(varchar(10), (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory));
SELECT ''ROWS_'' + (s.name COLLATE DATABASE_DEFAULT) + ''.'' + (t.name COLLATE DATABASE_DEFAULT) + ''='' + CONVERT(varchar(20), SUM(ps.row_count))
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.dm_db_partition_stats AS ps ON ps.object_id = t.object_id AND ps.index_id IN (0, 1)
WHERE t.is_ms_shipped = 0
GROUP BY s.name, t.name
ORDER BY s.name, t.name;';
END
"@
}

function Get-DbState($Ctx) {
    $kv = ConvertFrom-KeyValueLines (Invoke-DbSql -Ctx $Ctx -Sql (Get-DbStateSql $Ctx.Database))
    $rows = [ordered]@{}
    foreach ($k in @($kv.Keys)) { if ($k -clike 'ROWS_*') { $rows[$k.Substring(5)] = [long]$kv[$k] } }
    $files = @(foreach ($k in @($kv.Keys)) { if ($k -cmatch '^FILE_([DL])\d+$') { [pscustomobject]@{ Type = $Matches[1]; Path = $kv[$k] } } })
    $get = { param($key) if ($kv.Contains($key)) { $kv[$key] } else { $null } }
    return [pscustomobject]@{
        Exists         = ((& $get 'DB_EXISTS') -eq '1')
        EngineEdition  = [int](& $get 'ENGINE_EDITION')
        Edition        = & $get 'EDITION'
        Version        = & $get 'VERSION'
        DataPath       = & $get 'DATA_PATH'
        LogPath        = & $get 'LOG_PATH'
        State          = & $get 'DB_STATE'
        UserAccess     = & $get 'USER_ACCESS'
        Recovery       = & $get 'RECOVERY'
        LogReuseWait   = & $get 'LOG_REUSE_WAIT'
        MigrationHead  = & $get 'MIGRATION_HEAD'
        MigrationCount = & $get 'MIGRATION_COUNT'
        RowCounts      = $rows
        Files          = $files
    }
}

<#
  Resolves where backups live.
  local : host dir = server dir (default <repo>\backups\local); grants the SQL Server service account Modify on it.
  docker: host dir <repo>\backups, server dir /var/opt/mssql/backup; Mounted = whether the host dir is bind-mounted
          into the container (else files are moved with `docker compose cp`).
#>
function Get-BackupLocation {
    param([Parameter(Mandatory)]$Ctx, [string]$BackupDir, [switch]$ForWrite)

    if (-not $BackupDir) {
        $BackupDir = if ($Ctx.Target -eq 'local') { Join-Path $script:RepoRoot 'backups\local' } else { Join-Path $script:RepoRoot 'backups' }
    }
    Assert-Pattern $BackupDir $script:SafePathPattern 'backup directory'
    if (-not (Test-Path -LiteralPath $BackupDir)) { New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null }
    $BackupDir = (Resolve-Path -LiteralPath $BackupDir).Path

    # Keep backups out of git no matter where the root .gitignore stands.
    $rootBackups = Join-Path $script:RepoRoot 'backups'
    if ((Test-Path -LiteralPath $rootBackups) -and -not (Test-Path -LiteralPath (Join-Path $rootBackups '.gitignore'))) {
        [IO.File]::WriteAllText((Join-Path $rootBackups '.gitignore'), "# Backup files (*.bak + manifests) are never committed.`n*`n!.gitignore`n!.gitkeep`n")
    }

    if ($Ctx.Target -eq 'local') {
        if ($ForWrite) { Grant-SqlServiceAccess -Ctx $Ctx -Directory $BackupDir }
        return [pscustomobject]@{ HostDir = $BackupDir; ServerDir = $BackupDir; Separator = '\'; Mounted = $true }
    }

    Invoke-InContainer -Ctx $Ctx -Command @('mkdir', '-p', $script:ContainerBackupDir) | Out-Null
    $marker = '.mount-probe-' + [Guid]::NewGuid().ToString('N')
    $markerPath = Join-Path $BackupDir $marker
    [IO.File]::WriteAllText($markerPath, 'probe')
    try {
        $probe = Invoke-InContainer -Ctx $Ctx -Command @('test', '-f', "$($script:ContainerBackupDir)/$marker") -AllowFailure
        $mounted = ($probe.ExitCode -eq 0)
    }
    finally { Remove-Item -LiteralPath $markerPath -ErrorAction SilentlyContinue }
    return [pscustomobject]@{ HostDir = $BackupDir; ServerDir = $script:ContainerBackupDir; Separator = '/'; Mounted = $mounted }
}

# Local only: the SQL Server service (not the caller) writes the .bak, so it needs Modify on the folder.
function Grant-SqlServiceAccess {
    param($Ctx, [string]$Directory)
    $lines = Invoke-DbSql -Ctx $Ctx -Sql "SET NOCOUNT ON; SELECT TOP (1) 'SVC=' + (service_account COLLATE DATABASE_DEFAULT) FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%';"
    $svc = (ConvertFrom-KeyValueLines $lines)['SVC']
    if (-not $svc) { Write-Step 'Could not determine the SQL Server service account; make sure it can write to the backup folder.'; return }
    if ($svc -cnotmatch '^[A-Za-z0-9 ._\\$-]+$') { throw "Unexpected service account name '$svc'." }
    $acl = Get-Acl -LiteralPath $Directory
    $has = $acl.Access | Where-Object { [string]::Equals($_.IdentityReference.Value, $svc, [StringComparison]::OrdinalIgnoreCase) -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify }
    if (-not $has) {
        $r = Invoke-Native 'icacls.exe' @($Directory, '/grant', "${svc}:(OI)(CI)M")
        if ($r.ExitCode -ne 0) { throw "Granting $svc access to $Directory failed: $($r.Lines -join ' ')" }
        Write-Step "Granted '$svc' Modify on $Directory"
    }
}

function Join-ServerPath($Location, [string]$FileName) {
    Assert-Pattern $FileName $script:BackupFilePattern 'backup file name'
    return $Location.ServerDir.TrimEnd('\', '/') + $Location.Separator + $FileName
}

function Get-ManifestPath([string]$BakPath) { return [IO.Path]::ChangeExtension($BakPath, '.json') }

function Read-Manifest([string]$BakPath) {
    $m = Get-ManifestPath $BakPath
    if (-not (Test-Path -LiteralPath $m)) { return $null }
    $raw = Get-Content -LiteralPath $m -Raw -Encoding UTF8 | ConvertFrom-Json
    # Normalize: manifests from the compose sidecar carry fewer fields; StrictMode must not trip on them.
    $norm = [ordered]@{}
    foreach ($name in 'file', 'database', 'target', 'label', 'createdAtUtc', 'sha256', 'migrationHead', 'sizeBytes', 'checksumOk') {
        $prop = $raw.PSObject.Properties[$name]
        $norm[$name] = if ($prop) { $prop.Value } else { $null }
    }
    $rc = $raw.PSObject.Properties['rowCounts']
    $norm['rowCounts'] = if ($rc -and $rc.Value) { $rc.Value } else { New-Object psobject }
    return [pscustomobject]$norm
}

# "pre-deploy-1.2.0" -> "pre-deploy", "scheduled" -> "scheduled". Retention keeps the last N per family.
function Get-LabelFamily([string]$Label) {
    if (-not $Label) { return 'manual' }
    $m = [regex]::Match($Label, '^(.*?)(-v?\d.*)?$')
    if ($m.Success -and $m.Groups[1].Value) { return $m.Groups[1].Value }
    return $Label
}

# File name: <db>_<target>_<yyyyMMdd-HHmmss>Z_<label>.bak
function Get-BackupFiles([string]$HostDir, [string]$Database, [string]$Target) {
    $rx = '^' + [regex]::Escape($Database) + '_' + $Target + '_(\d{8}-\d{6})Z_(.+)\.bak$'
    return @(Get-ChildItem -LiteralPath $HostDir -Filter '*.bak' -File | ForEach-Object {
            $mm = [regex]::Match($_.Name, $rx)
            if ($mm.Success) {
                [pscustomobject]@{ File = $_; Stamp = $mm.Groups[1].Value; Label = $mm.Groups[2].Value; Family = (Get-LabelFamily $mm.Groups[2].Value) }
            }
        } | Sort-Object Stamp -Descending)
}

function Test-Interactive {
    if (-not [Environment]::UserInteractive) { return $false }
    $argsLine = [Environment]::GetCommandLineArgs() -join ' '
    return -not [regex]::IsMatch($argsLine, '-noni', [Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')
}
