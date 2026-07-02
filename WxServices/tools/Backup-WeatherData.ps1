#requires -Version 5
<#
.SYNOPSIS
  Backs up the WeatherData SQL Server (Express) database and copies the backup to
  one or more configured offsite destinations. WX-248.

.DESCRIPTION
  SIMPLE-recovery, full-by-default backup (optional differential). SQL Express has no
  SQL Agent, so this script is driven by a Windows Scheduled Task (see
  Register-BackupTask.ps1). Steps:
    1. BACKUP DATABASE ... WITH CHECKSUM  ->  a timestamped .bak/.dif in LocalStagingDir.
    2. RESTORE VERIFYONLY                 ->  confirm the backup is internally valid.
    3. Copy to each destination + verify  ->  a silently-failed offsite copy is worthless,
                                              so size AND SHA-256 are checked after copy.
    4. Retention prune                    ->  delete backups older than RetentionDays,
                                              locally and at each file destination.
  Any failure logs an ERROR (surfaced to log monitoring / Axel), drops a .FAILED sentinel,
  and exits non-zero. No silent failure.

  Offsite destinations are a pluggable, config-driven list keyed by Type. Only 'file'
  (a Windows path -- covers Dropbox / OneDrive / UNC shares; not user-mapped drives, since the
  task runs as SYSTEM) is implemented;
  'sftp' / 'ftp' / 's3' are recognized and throw a clear "not implemented" so the seam
  is obvious when we add them.

.PARAMETER ConfigPath
  Path to the JSON config. Defaults to Backup-Config.json beside this script.

.PARAMETER Type
  Full (default) or Diff (differential; requires a prior full).

.NOTES
  ASCII-only (Windows PowerShell 5.x reads ANSI). The SQL Server service account must be
  able to write to LocalStagingDir (BACKUP runs server-side).
#>
[CmdletBinding()]
param(
  [string]$ConfigPath = '',
  [ValidateSet('Full', 'Diff')]
  [string]$Type = 'Full'
)

$ErrorActionPreference = 'Stop'

# Resolve the config beside this script (in the body: $PSScriptRoot is reliable here in PS 5.x).
if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
  $here = if ($PSScriptRoot) { $PSScriptRoot }
          elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path }
          else { (Get-Location).Path }
  $ConfigPath = Join-Path $here 'Backup-Config.json'
}

# ---- logging (HarderWare format: UTC yyyy/MM/dd HH:mm:ss.fff LEVEL message) ----
$LogDir = 'C:\HarderWare\Logs'   # bootstrap default; re-pointed under InstallRoot after config load
$LogFile = Join-Path $LogDir 'weatherdata-backup.log'
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

function Write-Log {
  param([string]$Level, [string]$Message)
  $ts = (Get-Date).ToUniversalTime().ToString('yyyy/MM/dd HH:mm:ss.fff')
  $line = '{0} {1,-5} {2}' -f $ts, $Level, $Message
  # Best-effort file write, but never fully silent: surface a log-dir failure (perms/full disk)
  # to the warning stream so it leaves a trace instead of vanishing.
  try { Add-Content -Path $LogFile -Value $line } catch { Write-Warning "log write failed: $($_.Exception.Message)" }
  Write-Host $line
}

function Fail {
  param([string]$Message)
  Write-Log 'ERROR' $Message
  $sentinel = Join-Path $LogDir 'weatherdata-backup.FAILED'
  $stampMsg = (Get-Date).ToUniversalTime().ToString('o') + ' ' + $Message
  try { Set-Content -Path $sentinel -Value $stampMsg }
  catch {
    # Primary log dir unwritable -- drop a fallback sentinel in TEMP and surface to the error
    # stream so the failure is never fully silent (the non-zero exit is the last-resort signal).
    try { Set-Content -Path (Join-Path $env:TEMP 'weatherdata-backup.FAILED') -Value $stampMsg } catch {}
    try { Write-Error "backup FAILED; sentinel write to $sentinel failed: $Message" } catch {}
  }
  exit 1
}

# Safety net: honor the failure contract (ERROR log + .FAILED sentinel + non-zero exit) even
# for an UNHANDLED terminating error -- e.g. sqlcmd not on the Scheduled Task account's PATH,
# or a locked/AV-scanned .bak -- which would otherwise unwind past Fail with only a bad exit.
trap { Fail "unhandled: $($_.Exception.Message)" }

# ---- config ----
if (-not (Test-Path $ConfigPath)) { Fail "Config not found: $ConfigPath" }
try { $cfg = Get-Content -Raw -Path $ConfigPath | ConvertFrom-Json }
catch { Fail "Config is not valid JSON ($ConfigPath): $($_.Exception.Message)" }

$server = if ($cfg.SqlServer) { [string]$cfg.SqlServer } else { '.\SQLEXPRESS' }
$dbName = if ($cfg.Database) { [string]$cfg.Database } else { 'WeatherData' }
$retentionDays = [int]$cfg.RetentionDays
if ($retentionDays -le 0) { $retentionDays = 14 }

# InstallRoot mirrors the services' base (appsettings.shared.json); logs + default staging derive
# from it rather than hardcoding, per DEVELOPER-README. Re-point logging now that config is loaded.
$installRoot = if ($cfg.InstallRoot) { [string]$cfg.InstallRoot } else { 'C:\HarderWare' }
$LogDir = Join-Path $installRoot 'Logs'
$LogFile = Join-Path $LogDir 'weatherdata-backup.log'
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }

$staging = if ($cfg.LocalStagingDir) { [string]$cfg.LocalStagingDir } else { Join-Path $installRoot 'backups' }
if (-not (Test-Path $staging)) { New-Item -ItemType Directory -Path $staging -Force | Out-Null }

$ext = if ($Type -eq 'Diff') { 'dif' } else { 'bak' }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')
$fileName = '{0}-{1}-{2}.{3}' -f $dbName, $Type.ToLower(), $stamp, $ext
$bakPath = Join-Path $staging $fileName

Write-Log 'INFO' "Backup start: db=$dbName type=$Type server=$server -> $bakPath"

# ---- 1. BACKUP (server-side; CHECKSUM; new file per run) ----
# SQL Express has no backup compression, so none is requested.
$diffClause = if ($Type -eq 'Diff') { 'DIFFERENTIAL, ' } else { '' }
$bakEsc = $bakPath.Replace("'", "''")
$dbBracket = $dbName.Replace(']', ']]')   # safe bracketed identifier
$dbLit = $dbName.Replace("'", "''")       # safe string literal for NAME
$tsql = "SET NOCOUNT ON; BACKUP DATABASE [$dbBracket] TO DISK = N'$bakEsc' WITH $diffClause CHECKSUM, FORMAT, INIT, NAME = N'$dbLit-$Type-$stamp';"
& sqlcmd -S $server -E -C -b -x -Q $tsql 2>&1 | ForEach-Object { Write-Log 'INFO' "sqlcmd: $_" }
if ($LASTEXITCODE -ne 0) { Fail "BACKUP failed (sqlcmd exit $LASTEXITCODE)." }
if (-not (Test-Path $bakPath)) { Fail "BACKUP reported success but $bakPath is missing." }

# ---- 2. RESTORE VERIFYONLY ----
$verify = "RESTORE VERIFYONLY FROM DISK = N'$bakEsc' WITH CHECKSUM;"
& sqlcmd -S $server -E -C -b -x -Q $verify 2>&1 | ForEach-Object { Write-Log 'INFO' "verify: $_" }
if ($LASTEXITCODE -ne 0) { Fail "RESTORE VERIFYONLY failed for $bakPath (sqlcmd exit $LASTEXITCODE)." }
$srcHash = (Get-FileHash -Path $bakPath -Algorithm SHA256).Hash
$srcSize = (Get-Item $bakPath).Length
Write-Log 'INFO' ("Backup verified: {0:N0} bytes, SHA256 {1}" -f $srcSize, $srcHash)

# ---- 3. copy to destinations (verify size + hash) ----
function Copy-ToDestination {
  param($dest, [string]$src, [string]$name, [string]$expectHash, [long]$expectSize)
  $type = ([string]$dest.Type).ToLower()
  switch ($type) {
    'file' {
      $target = [string]$dest.Target
      if ([string]::IsNullOrWhiteSpace($target)) { throw "file destination missing 'Target'." }
      if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
      $dst = Join-Path $target $name
      Copy-Item -Path $src -Destination $dst -Force
      $dstHash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash
      $dstSize = (Get-Item $dst).Length
      if ($dstSize -ne $expectSize -or $dstHash -ne $expectHash) {
        throw "copy verification failed at '$dst' (size $dstSize/$expectSize, hash mismatch=$($dstHash -ne $expectHash))."
      }
      return $dst
    }
    { $_ -in @('sftp', 'ftp', 's3') } {
      throw "destination Type '$type' is a stub -- not implemented yet (WX-248 seam)."
    }
    default { throw "unknown destination Type '$type'." }
  }
}

$destinations = @($cfg.Destinations | Where-Object { $_ })
if ($destinations.Count -eq 0) { Fail 'Config.Destinations is missing/empty -- no offsite copy configured.' }
$copyFailures = 0
foreach ($dest in $destinations) {
  try {
    $dst = Copy-ToDestination -dest $dest -src $bakPath -name $fileName -expectHash $srcHash -expectSize $srcSize
    Write-Log 'INFO' "copied + verified -> $dst"
  }
  catch {
    $copyFailures++
    Write-Log 'ERROR' "destination copy failed: $($_.Exception.Message)"
  }
}
if ($copyFailures -gt 0) { Fail "$copyFailures of $($destinations.Count) destination copies failed." }

# ---- 4. retention prune (local staging + each file destination) ----
$cutoff = (Get-Date).AddDays(-$retentionDays)
function Remove-OldBackups {
  param([string]$dir)
  if (-not (Test-Path $dir)) { return }
  $files = @(Get-ChildItem -Path $dir -File -ErrorAction SilentlyContinue |
    Where-Object { ($_.Extension -eq '.bak' -or $_.Extension -eq '.dif') -and $_.Name -like "$dbName-*" })
  # Never prune a full base that a still-kept differential depends on: keep any full at or
  # older than the newest differential's timestamp (conservative diff-chain protection).
  $newestDiff = $files | Where-Object { $_.Extension -eq '.dif' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  foreach ($f in $files) {
    if ($f.LastWriteTime -ge $cutoff) { continue }
    if ($f.Extension -eq '.bak' -and $newestDiff -and $f.LastWriteTime -le $newestDiff.LastWriteTime) {
      Write-Log 'INFO' "retain (diff-chain base) $($f.FullName)"; continue
    }
    try { Remove-Item $f.FullName -Force; Write-Log 'INFO' "pruned $($f.FullName)" }
    catch { Write-Log 'WARN' "prune failed for $($f.FullName): $($_.Exception.Message)" }
  }
}
Remove-OldBackups -dir $staging
foreach ($dest in $destinations) {
  if (([string]$dest.Type).ToLower() -eq 'file' -and $dest.Target) { Remove-OldBackups -dir ([string]$dest.Target) }
}

# clear any stale failure sentinel on success
$sentinel = Join-Path $LogDir 'weatherdata-backup.FAILED'
if (Test-Path $sentinel) { Remove-Item $sentinel -Force }

Write-Log 'INFO' "Backup OK: $fileName -> staging + $($destinations.Count) destination(s), retention ${retentionDays}d."
exit 0
