# Shared helpers for scripts/db/*.ps1 (dot-sourced). Windows PowerShell 5.1 and PowerShell 7 compatible.
#
# Security rules followed here:
# - Every value that ends up inside T-SQL (database name, file names, paths, labels) is validated
#   against a strict regex first AND then emitted through ConvertTo-SqlLiteral / ConvertTo-SqlIdentifier
#   (quote doubling), never concatenated raw.
# - The SA password (docker target) is read from $env:MSSQL_SA_PASSWORD or .env, handed to sqlcmd only
#   through the SQLCMDPASSWORD environment variable of the child process and never printed.

Set-StrictMode -Version 3.0

$script:RepoRoot           = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
# dev | staging | prod: own compose project, env file and backup directory (scripts/_environments.ps1).
. (Join-Path (Join-Path $PSScriptRoot '..') '_environments.ps1')
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
    if ($env:LOCALAPPDATA) { $candidates += [IO.Path]::Combine($env:LOCALAPPDATA, 'Programs', 'DockerDesktop', 'resources', 'bin', 'docker.exe') }
    if ($env:ProgramFiles) { $candidates += [IO.Path]::Combine($env:ProgramFiles, 'Docker', 'Docker', 'resources', 'bin', 'docker.exe') }
    foreach ($c in $candidates) { if ($c -and (Test-Path -LiteralPath $c)) { return $c } }
    throw 'docker CLI not found (set DOCKER_EXE or add docker to PATH).'
}

# Reads one KEY=value from the process environment, else from the active environment's env file (.env, or .env.staging for
# staging). Returns $null when absent/empty. Never logs it.
function Get-DotEnvValue([string]$Name) {
    $fromEnv = [Environment]::GetEnvironmentVariable($Name)
    if ($fromEnv) { return $fromEnv }
    $envFile = Get-ActiveEnvFilePath
    if (-not (Test-Path -LiteralPath $envFile)) { return $null }
    foreach ($line in [IO.File]::ReadAllLines($envFile)) {
        if ($line -cmatch ('^\s*' + [regex]::Escape($Name) + '\s*=\s*(.*)$')) {
            $value = $Matches[1].Trim()
            if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            if ($value) { return $value }
        }
    }
    return $null
}

function Get-SaPassword {
    $v = Get-DotEnvValue 'MSSQL_SA_PASSWORD'
    if ($v) { return $v }
    throw 'MSSQL_SA_PASSWORD is neither in the environment nor in .env.'
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
        # Explicit -ComposeFile wins; otherwise the compose files of the active environment (dev: docker-compose.yml only;
        # staging/prod: + prod override). The compose project comes from COMPOSE_PROJECT_NAME (set by Use-StockEnvironment).
        $composeFiles = if ($ComposeFile) { @($ComposeFile) } else { @(Get-EnvironmentComposeFilePaths (Get-ActiveEnvironmentName)) }
        foreach ($cf in $composeFiles) { if (-not (Test-Path -LiteralPath $cf)) { throw "Compose file not found: $cf" } }
        $ctx.Runner = 'docker'
        $ctx.Docker = Get-DockerExe
        $ctx.ComposeArgs = @('compose', '--project-directory', $script:RepoRoot)
        foreach ($cf in $composeFiles) { $ctx.ComposeArgs += @('-f', (Resolve-Path -LiteralPath $cf).Path) }
        $ctx.Service = $Service
    }
    return [pscustomobject]$ctx
}

# docker target: the service container must be running (and healthy; waits up to $WaitSeconds while "starting") before any
# exec - otherwise fail with an actionable message instead of a raw "cannot exec in a stopped container" (BUG-12).
function Assert-DbContainerReady {
    param([Parameter(Mandatory)]$Ctx, [int]$WaitSeconds = 60)
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $why = ''
    do {
        $ps = Invoke-Native $Ctx.Docker ($Ctx.ComposeArgs + @('ps', '-a', '-q', $Ctx.Service))
        $cid = ($ps.Lines | Where-Object { $_ -cmatch '^[0-9a-f]{12,64}$' } | Select-Object -First 1)
        if (-not $cid) { $why = "no '$($Ctx.Service)' container exists"; break }
        $ins = Invoke-Native $Ctx.Docker @('inspect', '--format', '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}', $cid)
        $parts = (($ins.Lines -join '').Trim()).Split('|')
        if ($ins.ExitCode -ne 0 -or $parts.Count -lt 1) { $why = "cannot inspect '$($Ctx.Service)'"; break }
        $status = $parts[0]; $health = if ($parts.Count -gt 1) { $parts[1] } else { '' }
        if ($status -cne 'running') { $why = "container is '$status'"; break }
        if ($health -ceq '' -or $health -ceq 'healthy') { return }
        $why = "container health is '$health'"
        if ($health -ceq 'unhealthy') { break }
        Start-Sleep -Seconds 3
    } while ((Get-Date) -lt $deadline)
    throw "Service '$($Ctx.Service)' is not ready ($why). Start it and wait for healthy first: docker compose up -d $($Ctx.Service)  (check: docker compose ps, docker compose logs $($Ctx.Service))."
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
            Assert-DbContainerReady -Ctx $Ctx
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
    Assert-DbContainerReady -Ctx $Ctx
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

    $envName = Get-ActiveEnvironmentName
    if (-not $BackupDir) { $BackupDir = Get-EnvironmentBackupDir $envName $Ctx.Target }
    Assert-Pattern $BackupDir $script:SafePathPattern 'backup directory'
    # Isolation guard: an environment only reads/writes/prunes inside its own backup directory (checked before anything is created).
    Assert-BackupDirIsolated $envName $BackupDir
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

# Retention: keeps the newest $Keep backups of label family $Family INSIDE $HostDir and deletes the rest (+ manifests).
# Never leaves the environment's backup directory: the directory is re-checked by the isolation guard and every file to delete
# must be a direct child of it. Returns the removed file names.
function Invoke-BackupRetention {
    param([Parameter(Mandatory)][string]$HostDir, [Parameter(Mandatory)][string]$Database, [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][string]$Family, [Parameter(Mandatory)][int]$Keep, [string]$Environment)
    if ($Keep -le 0) { return @() }
    if (-not $Environment) { $Environment = Get-ActiveEnvironmentName }
    Assert-BackupDirIsolated $Environment $HostDir
    $dirFull = [IO.Path]::GetFullPath($HostDir).TrimEnd('\', '/')
    $removed = @()
    $old = @(Get-BackupFiles -HostDir $HostDir -Database $Database -Target $Target | Where-Object { $_.Family -eq $Family } | Select-Object -Skip $Keep)
    foreach ($o in $old) {
        $parent = [IO.Path]::GetFullPath($o.File.DirectoryName).TrimEnd('\', '/')
        if (-not $parent.Equals($dirFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Backup isolation guard: refusing to delete $($o.File.FullName) (outside $dirFull)." }
        Remove-Item -LiteralPath $o.File.FullName -Force
        $j = Get-ManifestPath $o.File.FullName
        if (Test-Path -LiteralPath $j) { Remove-Item -LiteralPath $j -Force }
        Write-Step "Retention ($Family, keep $Keep): removed $($o.File.Name)"
        $removed += $o.File.Name
    }
    return $removed
}

function Test-Interactive {
    if (-not [Environment]::UserInteractive) { return $false }
    $argsLine = [Environment]::GetCommandLineArgs() -join ' '
    return -not [regex]::IsMatch($argsLine, '-noni', [Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')
}

# ---------------------------------------------------------------------------------------------------------------
# API cache flush after a restore (BUG-9). The API caches stock/account DTOs and lists in Redis under the instance
# prefix "sa:". After the database goes back in time, every one of those entries is stale and, because identity
# values are rolled back too, a cached id could even be served for a different row. Only keys with the app prefix
# are removed (SCAN + UNLINK in batches); FLUSHALL/FLUSHDB are never used.
# Result: Status = Flushed | Unreachable | Failed, Deleted = number of keys removed, Message.
# ---------------------------------------------------------------------------------------------------------------
$script:CachePrefixPattern = '^[A-Za-z0-9_.-]{1,32}:$'
$script:RedisHostPattern   = '^[A-Za-z0-9.-]{1,253}$'

function Clear-AppCacheDocker {
    param([Parameter(Mandatory)]$Ctx, [string]$Prefix = 'sa:', [string]$RedisService = 'redis')
    Assert-Pattern $Prefix $script:CachePrefixPattern 'cache prefix'
    Assert-Pattern $RedisService $script:ServicePattern 'redis compose service'

    $ps = Invoke-Native $Ctx.Docker ($Ctx.ComposeArgs + @('ps', '--status', 'running', '--services'))
    if ($ps.ExitCode -ne 0 -or -not ($ps.Lines | Where-Object { $_.Trim() -ceq $RedisService })) {
        return [pscustomobject]@{ Status = 'Unreachable'; Deleted = 0; Message = "compose service '$RedisService' is not running" }
    }
    # Pattern is validated above; it is the only interpolated value. No double quotes (PS 5.1 native-arg quoting).
    $pattern = $Prefix + '*'
    $sh = 'n=$(redis-cli --scan --pattern ''{PATTERN}'' --count 1000 | wc -l) && ' +
          'redis-cli --scan --pattern ''{PATTERN}'' --count 1000 | tr ''\n'' ''\000'' | xargs -0 -r -n 500 redis-cli unlink > /dev/null && ' +
          'echo FLUSHED=$n && echo REMAINING=$(redis-cli --scan --pattern ''{PATTERN}'' | wc -l)'
    $sh = $sh.Replace('{PATTERN}', $pattern)
    $r = Invoke-Native $Ctx.Docker ($Ctx.ComposeArgs + @('exec', '-T', $RedisService, 'sh', '-c', $sh))
    $kv = ConvertFrom-KeyValueLines $r.Lines
    if ($r.ExitCode -ne 0 -or -not $kv.Contains('FLUSHED')) {
        return [pscustomobject]@{ Status = 'Failed'; Deleted = 0; Message = ($r.Lines -join ' ') }
    }
    return [pscustomobject]@{ Status = 'Flushed'; Deleted = [int]$kv['FLUSHED']; Message = "remaining $($kv['REMAINING'])" }
}

# Minimal RESP client (no redis-cli needed on Windows). Optional password: $env:REDIS_PASSWORD.
function Clear-AppCacheTcp {
    param([string]$RedisHost = 'localhost', [int]$Port = 6379, [string]$Prefix = 'sa:', [int]$TimeoutMs = 2000)
    Assert-Pattern $RedisHost $script:RedisHostPattern 'redis host'
    Assert-Pattern $Prefix $script:CachePrefixPattern 'cache prefix'
    if ($Port -lt 1 -or $Port -gt 65535) { throw "Invalid redis port $Port" }

    $client = New-Object Net.Sockets.TcpClient
    try {
        try {
            if (-not $client.ConnectAsync($RedisHost, $Port).Wait($TimeoutMs)) { throw 'connect timeout' }
        }
        catch {
            return [pscustomobject]@{ Status = 'Unreachable'; Deleted = 0; Message = "Redis ${RedisHost}:$Port not reachable" }
        }
        $client.ReceiveTimeout = 10000; $client.SendTimeout = 10000
        $stream = New-Object IO.BufferedStream($client.GetStream())
        $utf8 = New-Object Text.UTF8Encoding($false)

        $send = {
            param([string[]]$parts)
            $sb = New-Object Text.StringBuilder
            [void]$sb.Append('*').Append($parts.Count).Append("`r`n")
            foreach ($p in $parts) { [void]$sb.Append('$').Append($utf8.GetByteCount($p)).Append("`r`n").Append($p).Append("`r`n") }
            $bytes = $utf8.GetBytes($sb.ToString())
            $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
        }
        $readLine = {
            $buf = New-Object IO.MemoryStream
            while ($true) {
                $b = $stream.ReadByte()
                if ($b -lt 0) { throw 'connection closed by Redis' }
                if ($b -eq 13) { [void]$stream.ReadByte(); break }
                $buf.WriteByte([byte]$b)
            }
            $utf8.GetString($buf.ToArray())
        }
        $read = $null
        $read = {
            $line = & $readLine
            $type = $line.Substring(0, 1); $body = $line.Substring(1)
            switch ($type) {
                '+' { return $body }
                '-' { throw "Redis error: $body" }
                ':' { return [long]$body }
                '$' {
                    $len = [int]$body
                    if ($len -lt 0) { return $null }
                    $data = New-Object byte[] ($len + 2); $off = 0
                    while ($off -lt $data.Length) { $n = $stream.Read($data, $off, $data.Length - $off); if ($n -le 0) { throw 'connection closed' }; $off += $n }
                    return $utf8.GetString($data, 0, $len)
                }
                '*' {
                    $count = [int]$body
                    $items = New-Object Collections.ArrayList
                    for ($i = 0; $i -lt $count; $i++) { [void]$items.Add((& $read)) }
                    return , $items.ToArray()
                }
                default { throw "Unexpected Redis reply '$line'" }
            }
        }

        if ($env:REDIS_PASSWORD) { & $send @('AUTH', $env:REDIS_PASSWORD); [void](& $read) }
        $cursor = '0'; $deleted = 0
        do {
            & $send @('SCAN', $cursor, 'MATCH', ($Prefix + '*'), 'COUNT', '1000')
            $reply = & $read
            $cursor = [string]$reply[0]
            $keys = @($reply[1])
            for ($i = 0; $i -lt $keys.Count; $i += 500) {
                $batch = @($keys[$i..([Math]::Min($i + 499, $keys.Count - 1))])
                & $send (@('UNLINK') + $batch)
                $deleted += [int](& $read)
            }
        } while ($cursor -ne '0')
        return [pscustomobject]@{ Status = 'Flushed'; Deleted = $deleted; Message = "${RedisHost}:$Port" }
    }
    catch {
        return [pscustomobject]@{ Status = 'Failed'; Deleted = 0; Message = $_.Exception.Message }
    }
    finally { $client.Close() }
}

# ---------------------------------------------------------------------------------------------------------------
# Off-site backup (rclone in a pinned container -> any S3-compatible bucket). Secrets travel only as environment
# variables of the docker CLI process ("-e NAME" without a value), never on a command line, in a file or in output.
# ---------------------------------------------------------------------------------------------------------------
$script:RcloneImage = 'rclone/rclone:1.68.2@sha256:74c51b8817e5431bd6d7ed27cb2a50d8ee78d77f6807b72a41ef6f898845942b'

function Test-OffsiteEnabled {
    $v = Get-DotEnvValue 'OFFSITE_ENABLED'
    return [bool]($v -and (@('true', '1', 'yes', 'on') -contains $v.Trim().ToLowerInvariant()))
}

# Reads + validates OFFSITE_* from the environment / .env. Throws on a missing or malformed setting.
function Get-OffsiteConfig {
    $c = [ordered]@{
        Endpoint      = (Get-DotEnvValue 'OFFSITE_S3_ENDPOINT')
        Region        = (Get-DotEnvValue 'OFFSITE_S3_REGION')
        Bucket        = (Get-DotEnvValue 'OFFSITE_S3_BUCKET')
        AccessKey     = (Get-DotEnvValue 'OFFSITE_S3_ACCESS_KEY')
        SecretKey     = (Get-DotEnvValue 'OFFSITE_S3_SECRET_KEY')
        Prefix        = (Get-DotEnvValue 'OFFSITE_PREFIX')
        RetentionDays = (Get-DotEnvValue 'OFFSITE_RETENTION_DAYS')
        MinKeep       = (Get-DotEnvValue 'OFFSITE_MIN_KEEP')
    }
    if ($null -eq $c.Prefix) { $c.Prefix = if ((Get-ActiveEnvironmentName) -ceq 'staging') { 'stockaccount-staging' } else { 'stockaccount' } }
    if (-not $c.RetentionDays) { $c.RetentionDays = '30' }
    if (-not $c.MinKeep) { $c.MinKeep = '3' }
    $missing = @('Bucket', 'AccessKey', 'SecretKey' | Where-Object { -not $c[$_] })
    if ($missing.Count) { throw "Off-site backup is not configured: set OFFSITE_S3_BUCKET, OFFSITE_S3_ACCESS_KEY and OFFSITE_S3_SECRET_KEY in .env (missing: $($missing -join ', '))." }
    Assert-Pattern $c.Bucket '^[a-z0-9][a-z0-9.-]{1,62}$' 'OFFSITE_S3_BUCKET'
    if ($c.Prefix -cnotmatch '^[A-Za-z0-9._/-]*$' -or $c.Prefix.Contains('..') -or $c.Prefix.StartsWith('/')) { throw "Invalid OFFSITE_PREFIX '$($c.Prefix)'." }
    if ($c.Endpoint) { Assert-Pattern $c.Endpoint '^https?://[A-Za-z0-9.:_-]+/?$' 'OFFSITE_S3_ENDPOINT' }
    if ($c.Region) { Assert-Pattern $c.Region '^[A-Za-z0-9-]{1,32}$' 'OFFSITE_S3_REGION' }
    Assert-Pattern $c.RetentionDays '^\d{1,5}$' 'OFFSITE_RETENTION_DAYS'
    Assert-Pattern $c.MinKeep '^\d{1,4}$' 'OFFSITE_MIN_KEEP'
    return [pscustomobject]$c
}

# Runs `docker run --rm <rclone image>` with the S3 settings as RCLONE_CONFIG_OFFSITE_* env vars and $HostDir at /data.
# $ContainerArgs is everything after the image name; $StdinText (optional) is piped in (used to run offsite-sync.sh).
# OFFSITE_DOCKER_BACKUP_DIR (optional) = the path the Docker daemon sees for $HostDir (when this script itself runs
# inside a container that talks to the host's Docker socket); OFFSITE_DOCKER_NETWORK (optional) = --network.
function Invoke-OffsiteContainer {
    param(
        [Parameter(Mandatory)]$Config,
        [Parameter(Mandatory)][string]$HostDir,
        [Parameter(Mandatory)][string[]]$ContainerArgs,
        [string]$StdinText = $null,
        [switch]$ReadOnlyData,
        [string]$Entrypoint
    )
    $docker = Get-DockerExe
    $mountDir = if ($env:OFFSITE_DOCKER_BACKUP_DIR) { $env:OFFSITE_DOCKER_BACKUP_DIR } else { (Resolve-Path -LiteralPath $HostDir).Path }
    if ($mountDir -cmatch '[,"\r\n]') { throw "Unsafe backup directory path for docker: $mountDir" }
    $vars = [ordered]@{
        RCLONE_CONFIG_OFFSITE_TYPE              = 's3'
        RCLONE_CONFIG_OFFSITE_ACCESS_KEY_ID     = $Config.AccessKey
        RCLONE_CONFIG_OFFSITE_SECRET_ACCESS_KEY = $Config.SecretKey
        RCLONE_CONFIG_OFFSITE_ENDPOINT          = $Config.Endpoint
        RCLONE_CONFIG_OFFSITE_REGION            = $Config.Region
        RCLONE_CONFIG_OFFSITE_PROVIDER          = $(if (-not $Config.Endpoint) { 'AWS' } elseif ($Config.Endpoint -cmatch '\.r2\.cloudflarestorage\.com') { 'Cloudflare' } else { 'Other' })
        RCLONE_CONFIG_OFFSITE_NO_CHECK_BUCKET   = 'true'
        OFFSITE_S3_BUCKET                       = $Config.Bucket
        OFFSITE_PREFIX                          = $Config.Prefix
        OFFSITE_RETENTION_DAYS                  = $Config.RetentionDays
        OFFSITE_MIN_KEEP                        = $Config.MinKeep
    }
    $dargs = @('run', '--rm')
    if ($null -ne $StdinText) { $dargs += '-i' }
    if ($env:OFFSITE_DOCKER_NETWORK) {
        Assert-Pattern $env:OFFSITE_DOCKER_NETWORK '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$' 'OFFSITE_DOCKER_NETWORK'
        $dargs += @('--network', $env:OFFSITE_DOCKER_NETWORK)
    }
    foreach ($k in $vars.Keys) { $dargs += @('-e', $k) }
    $mountSpec = $mountDir + ':/data'
    if ($ReadOnlyData) { $mountSpec += ':ro' }
    $dargs += @('-v', $mountSpec)
    if ($Entrypoint) { $dargs += @('--entrypoint', $Entrypoint) }
    $dargs += $script:RcloneImage
    $dargs += $ContainerArgs

    $saved = @{}
    foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$vars[$k]) }
    try { return (Invoke-Native $docker $dargs $StdinText) }
    finally { foreach ($k in $vars.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
}

# Prints container output; defence in depth: the keys are masked if they ever show up.
function Write-OffsiteLines($Config, [string[]]$Lines) {
    foreach ($l in $Lines) {
        $t = "$l"
        foreach ($s in @($Config.SecretKey, $Config.AccessKey)) { if ($s) { $t = $t.Replace($s, '***') } }
        if ($t.Trim()) { Write-Host $t }
    }
}
