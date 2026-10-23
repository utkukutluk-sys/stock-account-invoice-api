<#
.SYNOPSIS
  Downloads a backup (.bak + .json manifest) from the off-site bucket into ./backups and verifies its SHA-256
  against the manifest. The operator then runs restore.ps1 -File <name> on it (this script never touches a database).

.DESCRIPTION
  Use it on a new/rebuilt server after a disaster, or to fetch an older backup that was pruned locally.
  Needs only Docker and the OFFSITE_S3_* settings in .env (or the environment); rclone runs in a pinned container.
  The files are downloaded into a temporary sub-folder, checked, and only then moved next to the other backups, so a
  failed or tampered download never leaves a half-file behind. An existing file with the same name is kept unless -Force.
  Selection: -File <name> | -Latest [-Label <label|family>] | -List (just show what the bucket holds).
  -Latest ignores "pre-restore" safety backups unless -Label pre-restore is given.
  Exit codes: 0 ok, 1 failure (including checksum mismatch), 2 not configured / nothing found.

.EXAMPLE
  ./scripts/db/offsite-restore.ps1 -List
.EXAMPLE
  ./scripts/db/offsite-restore.ps1 -Latest -Label scheduled
  ./scripts/db/restore.ps1 -Target docker -File StockAccountDb_docker_20261023-020000Z_scheduled.bak -Force
#>
[CmdletBinding(DefaultParameterSetName = 'Latest')]
param(
    [ValidateSet('local', 'docker')][string]$Target = 'docker',
    [string]$Database = 'StockAccountDb',
    [Parameter(ParameterSetName = 'File', Mandatory)][string]$File,
    [Parameter(ParameterSetName = 'Latest')][switch]$Latest,
    [Parameter(ParameterSetName = 'Latest')][string]$Label,
    [Parameter(ParameterSetName = 'List')][switch]$List,
    [string]$BackupDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$exitCode = 1
$tmpName = $null
$dir = $null
try {
    Assert-Pattern $Database $script:DbNamePattern 'database name'
    try { $cfg = Get-OffsiteConfig }
    catch { Write-Host "[offsite] $($_.Exception.Message)" -ForegroundColor Red; $exitCode = 2; throw [OperationCanceledException]::new() }

    $dir = if ($BackupDir) { $BackupDir } elseif ($Target -eq 'local') { Join-Path (Join-Path $script:RepoRoot 'backups') 'local' } else { Join-Path $script:RepoRoot 'backups' }
    Assert-Pattern $dir $script:SafePathPattern 'backup directory'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

    # ---- what is in the bucket? ----
    $engine = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'offsite-sync.sh')).Replace("`r`n", "`n")
    $ls = Invoke-OffsiteContainer -Config $cfg -HostDir $dir -ReadOnlyData -Entrypoint 'sh' -ContainerArgs @('-s', '--', 'list') -StdinText $engine
    if ($ls.ExitCode -ne 0) { Write-OffsiteLines $cfg $ls.Lines; throw "Listing the bucket failed (exit $($ls.ExitCode))." }
    $rx = '^' + [regex]::Escape($Database) + '_(local|docker)_(\d{8}-\d{6})Z_(.+)\.bak$'
    $remote = @(foreach ($n in $ls.Lines) {
            $name = "$n".Trim()
            $m = [regex]::Match($name, $rx)
            if ($m.Success -and $name -cmatch $script:BackupFilePattern) {
                [pscustomobject]@{ Name = $name; Target = $m.Groups[1].Value; Stamp = $m.Groups[2].Value; Label = $m.Groups[3].Value; Family = (Get-LabelFamily $m.Groups[3].Value) }
            }
        })
    $remote = @($remote | Where-Object { $_.Target -ceq $Target } | Sort-Object Stamp -Descending)

    if ($List) {
        if ($remote.Count -eq 0) { Write-Step "No '$Database' ($Target) backups in bucket '$($cfg.Bucket)' prefix '$($cfg.Prefix)'." }
        foreach ($r in $remote) { Write-Host ("{0}  {1}  {2}" -f $r.Stamp, $r.Label.PadRight(24), $r.Name) }
        $exitCode = 0; throw [OperationCanceledException]::new()
    }

    if ($PSCmdlet.ParameterSetName -eq 'File') {
        $name = Split-Path -Leaf $File
        if ($name -cnotmatch $script:BackupFilePattern) { throw "Invalid backup file name '$File'." }
        if (-not ($remote | Where-Object { $_.Name -ceq $name })) { throw "'$name' is not in the bucket (run with -List to see what is there)." }
    }
    else {
        $cand = $remote
        if ($Label) {
            Assert-Pattern $Label $script:LabelPattern 'label'
            $cand = @($cand | Where-Object { $_.Label -ceq $Label -or $_.Family -ceq $Label })
        }
        $cand = @($cand | Where-Object { $_.Family -cne 'pre-restore' -or $Label -ceq 'pre-restore' })
        if ($cand.Count -eq 0) { Write-Host "[offsite] No matching '$Database' ($Target) backup in the bucket$(if ($Label) { " with label '$Label'" })." -ForegroundColor Red; $exitCode = 2; throw [OperationCanceledException]::new() }
        $name = $cand[0].Name
    }
    $jsonName = [IO.Path]::ChangeExtension($name, '.json')

    $finalBak = Join-Path $dir $name
    $finalJson = Join-Path $dir $jsonName
    if ((Test-Path -LiteralPath $finalBak) -and -not $Force) { throw "$finalBak already exists (use -Force to replace it)." }

    # ---- download into a temp sub-folder, verify, then move ----
    $tmpName = '.offsite-dl-' + [Guid]::NewGuid().ToString('N')
    $tmpDir = Join-Path $dir $tmpName
    New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
    $base = if ($cfg.Prefix) { "offsite:$($cfg.Bucket)/$($cfg.Prefix)" } else { "offsite:$($cfg.Bucket)" }
    Write-Step "Downloading $name (+ manifest) from bucket '$($cfg.Bucket)'"
    foreach ($f in $jsonName, $name) {   # manifest first: it is tiny and the .bak is useless without it
        $d = Invoke-OffsiteContainer -Config $cfg -HostDir $dir -ContainerArgs @('copyto', "$base/$f", "/data/$tmpName/$f", '--no-traverse', '--s3-no-check-bucket', '--retries', '3', '--log-level', 'ERROR')
        if ($d.ExitCode -ne 0) { Write-OffsiteLines $cfg $d.Lines; throw "Download of $f failed (exit $($d.ExitCode))." }
    }
    $tmpBak = Join-Path $tmpDir $name
    $tmpJson = Join-Path $tmpDir $jsonName
    if (-not (Test-Path -LiteralPath $tmpBak) -or -not (Test-Path -LiteralPath $tmpJson)) { throw 'Download finished but a file is missing.' }

    $manifest = Get-Content -LiteralPath $tmpJson -Raw -Encoding UTF8 | ConvertFrom-Json
    $expected = "$($manifest.sha256)".ToLowerInvariant()
    if ($expected -cnotmatch '^[0-9a-f]{64}$') { throw 'The manifest has no valid sha256; refusing the download.' }
    $sha = (Get-FileHash -LiteralPath $tmpBak -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha -cne $expected) { throw "SHA-256 MISMATCH for $name (bucket copy corrupted or tampered); nothing was kept." }
    if ($manifest.sizeBytes -and (Get-Item -LiteralPath $tmpBak).Length -ne [long]$manifest.sizeBytes) { throw "Size mismatch for $name; nothing was kept." }

    Move-Item -LiteralPath $tmpBak -Destination $finalBak -Force
    Move-Item -LiteralPath $tmpJson -Destination $finalJson -Force
    Write-Step ("OK {0} downloaded to {1} (sha256 {2}... verified, label '{3}', created {4}, migration head {5})" -f $name, $dir, $sha.Substring(0, 12), $manifest.label, $manifest.createdAtUtc, $manifest.migrationHead)
    Write-Step "Next: ./scripts/db/restore.ps1 -Target $Target -File $name -Force"
    [pscustomobject]@{ file = $name; path = $finalBak; sha256 = $sha }
    $exitCode = 0
}
catch [OperationCanceledException] { }
catch {
    Write-Host "[offsite] RESTORE-DOWNLOAD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($tmpName -and $dir) { Remove-Item -LiteralPath (Join-Path $dir $tmpName) -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
