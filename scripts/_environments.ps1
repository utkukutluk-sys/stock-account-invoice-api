# Environment definitions shared by scripts/deploy/*.ps1 and scripts/db/*.ps1 (dot-sourced). Windows PowerShell 5.1 and
# PowerShell 7 compatible. dev | staging | prod each get their OWN compose project, env file, release history and backup
# directory, so an operation on one environment can never touch the files or containers of another.
#
#   env      compose project         compose files                          env file        backups              release state
#   dev      stockaccount (default)  docker-compose.yml                     .env            backups (+ local)    .state/releases.json
#   staging  stockaccount-staging    yml + prod.yml + staging.yml           .env.staging    backups/staging      .state/staging/releases.json
#   prod     stockaccount-prod       yml + prod.yml                         .env            BACKUP_DIR|backups/prod  .state/prod/releases.json

# STOCKACCOUNT_ENV_ROOT is a TEST hook: tests/prod-readiness/backup-isolation.test.ps1 runs the guard against a throw-away tree.
$script:EnvRepoRoot = if ($env:STOCKACCOUNT_ENV_ROOT) { $env:STOCKACCOUNT_ENV_ROOT } else { (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$script:EnvironmentNames = @('dev', 'staging', 'prod')

function Get-EnvironmentDefinition([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name)) { $Name = 'dev' }
    $Name = $Name.Trim().ToLowerInvariant()
    switch ($Name) {
        'dev' {
            return [pscustomobject]@{ Name = 'dev'; Project = 'stockaccount'; ComposeFiles = @('docker-compose.yml'); EnvFile = '.env'
                BackupSubDir = $null; StateSubDir = $null; ReadyUrl = 'http://localhost:5080/health/ready'; Production = $false }
        }
        'staging' {
            return [pscustomobject]@{ Name = 'staging'; Project = 'stockaccount-staging'
                ComposeFiles = @('docker-compose.yml', 'docker-compose.prod.yml', 'docker-compose.staging.yml'); EnvFile = '.env.staging'
                BackupSubDir = 'backups/staging'; StateSubDir = 'staging'; ReadyUrl = 'internal'; Production = $true }
        }
        'prod' {
            return [pscustomobject]@{ Name = 'prod'; Project = 'stockaccount-prod'
                ComposeFiles = @('docker-compose.yml', 'docker-compose.prod.yml'); EnvFile = '.env'
                BackupSubDir = 'backups/prod'; StateSubDir = 'prod'; ReadyUrl = 'internal'; Production = $true }
        }
        default { throw "Unknown environment '$Name' (expected dev, staging or prod)." }
    }
}

# Active environment of this process: set by Use-StockEnvironment (deploy/rollback/backup/restore), default dev.
function Get-ActiveEnvironmentName {
    $v = $env:STOCKACCOUNT_ENV
    if ([string]::IsNullOrWhiteSpace($v)) { return 'dev' }
    return $v.Trim().ToLowerInvariant()
}

# Points this process (and every child docker/pwsh call) at one environment: compose project + env file. The compose FILE list
# is exported by deploy's Set-ComposeFiles (it may add drill overrides); the db scripts pass the list with explicit -f.
function Use-StockEnvironment([string]$Name) {
    $def = Get-EnvironmentDefinition $Name
    $env:STOCKACCOUNT_ENV = $def.Name
    if ($def.Name -ceq 'dev') {
        # dev keeps the historic behaviour: COMPOSE_PROJECT_NAME may name a second local stack, otherwise the "name:" of the compose file.
        Remove-Item Env:COMPOSE_ENV_FILES -ErrorAction SilentlyContinue
    }
    else {
        $env:COMPOSE_PROJECT_NAME = $def.Project
        if ($def.EnvFile -cne '.env') {
            $envFile = Join-Path $script:EnvRepoRoot $def.EnvFile
            if (-not (Test-Path -LiteralPath $envFile)) { throw "$($def.EnvFile) not found: run scripts/deploy/new-secrets.ps1 -Environment $($def.Name) first." }
            $env:COMPOSE_ENV_FILES = $envFile
        }
        else { Remove-Item Env:COMPOSE_ENV_FILES -ErrorAction SilentlyContinue }
    }
    return $def
}

# Absolute env file path of the active environment (.env for dev/prod, .env.staging for staging).
function Get-ActiveEnvFilePath {
    $def = Get-EnvironmentDefinition (Get-ActiveEnvironmentName)
    return (Join-Path $script:EnvRepoRoot $def.EnvFile)
}

function Get-EnvironmentComposeFilePaths([string]$Name) {
    $def = Get-EnvironmentDefinition $Name
    return @($def.ComposeFiles | ForEach-Object { Join-Path $script:EnvRepoRoot $_ })
}

# Value of KEY from an env file (no process environment); $null when absent/empty. Quotes are stripped.
function Read-EnvFileValue([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -cmatch ('^\s*' + [regex]::Escape($Name) + '\s*=\s*(.*?)\s*$')) {
            $value = $Matches[1]
            if ($value.Length -ge 2 -and (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
        }
    }
    return $null
}

# ---------------------------------------------------------------------------------------------------------------
# Backup directory isolation. Every environment owns one directory; backup/restore/retention/off-site only operate there.
#   dev     : <repo>/backups (compose bind mount) and <repo>/backups/local (-Target local). Never inside backups/staging|prod.
#   staging : <repo>/backups/staging (or a sub folder of it).
#   prod    : BACKUP_DIR from the environment / .env when set, else <repo>/backups/prod. Never <repo>/backups itself.
# ---------------------------------------------------------------------------------------------------------------
function Test-PathInside([string]$Path, [string]$Parent) {
    $p = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $r = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    if ($p.Equals($r, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $sep = [string][IO.Path]::DirectorySeparatorChar
    return $p.StartsWith($r + $sep, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($r + '/', [StringComparison]::OrdinalIgnoreCase)
}

function Get-ConfiguredBackupDir {
    $v = [Environment]::GetEnvironmentVariable('BACKUP_DIR')
    if ([string]::IsNullOrWhiteSpace($v)) { $v = Read-EnvFileValue (Join-Path $script:EnvRepoRoot '.env') 'BACKUP_DIR' }
    if ([string]::IsNullOrWhiteSpace($v)) { return $null }
    if (-not [IO.Path]::IsPathRooted($v)) { $v = Join-Path $script:EnvRepoRoot $v }
    return [IO.Path]::GetFullPath($v)
}

# Default host backup directory of an environment ($Target 'local' = the dev-only SQL Express folder).
function Get-EnvironmentBackupDir([string]$Name, [string]$Target = 'docker') {
    $def = Get-EnvironmentDefinition $Name
    switch ($def.Name) {
        'dev' { return $(if ($Target -ceq 'local') { Join-Path $script:EnvRepoRoot 'backups/local' } else { Join-Path $script:EnvRepoRoot 'backups' }) }
        'staging' { return (Join-Path $script:EnvRepoRoot 'backups/staging') }
        'prod' { $c = Get-ConfiguredBackupDir; if ($c) { return $c } else { return (Join-Path $script:EnvRepoRoot 'backups/prod') } }
    }
}

# $null when $Dir is a legal backup directory for environment $Name, otherwise the reason it is not.
function Get-BackupDirViolation([string]$Name, [string]$Dir) {
    $def = Get-EnvironmentDefinition $Name
    $full = [IO.Path]::GetFullPath($Dir)
    $root = Join-Path $script:EnvRepoRoot 'backups'
    $stagingDir = Join-Path $script:EnvRepoRoot 'backups/staging'
    $prodDir = Get-EnvironmentBackupDir 'prod'
    switch ($def.Name) {
        'dev' {
            if (Test-PathInside $full $stagingDir) { return "dev must not use the staging backup directory ($stagingDir)" }
            if (Test-PathInside $full (Join-Path $script:EnvRepoRoot 'backups/prod')) { return 'dev must not use the prod backup directory (backups/prod)' }
            if (Test-PathInside $full $prodDir) { return "dev must not use the prod backup directory ($prodDir)" }
        }
        'staging' {
            if (-not (Test-PathInside $full $stagingDir)) { return "staging backups must live inside $stagingDir (got $full); dev files in $root can never be touched by staging" }
        }
        'prod' {
            if ($full.TrimEnd('\', '/').Equals($root.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) { return "prod must not use the dev backup directory $root" }
            if (Test-PathInside $full $stagingDir) { return "prod must not use the staging backup directory ($stagingDir)" }
            if (Test-PathInside $full (Join-Path $root 'local')) { return 'prod must not use backups/local' }
        }
    }
    return $null
}

function Assert-BackupDirIsolated([string]$Name, [string]$Dir) {
    $why = Get-BackupDirViolation $Name $Dir
    if ($why) { throw "Backup isolation guard: $why." }
}
