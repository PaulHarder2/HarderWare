#requires -Version 5
<#
.SYNOPSIS
  Boot-time reconcile for the autoheal sidecar (WX-68). Waits for the Docker engine to
  come up after a host reboot, then force-recreates the autoheal container so its Docker
  socket mount is re-minted. Registered as a logon-triggered scheduled task by
  Register-AutohealTask.ps1.

.DESCRIPTION
  WHY THIS EXISTS. autoheal's only mount is the Docker socket - a single FILE. On Docker
  Desktop (Windows/WSL2) a host reboot auto-starts restart:unless-stopped containers before
  the socket source is resolvable, and the daemon's own restart reuses the container's baked
  (now-stale) bind-mount proxy path -> the container dies with exit 127 ("mounting ... not a
  directory") and RestartCount stays 0. unless-stopped cannot heal a mount-CREATE failure;
  only a `docker compose up --force-recreate` re-mints a fresh, valid bind-mount source. The
  four service containers survive because their mount is a DIRECTORY (C:\HarderWare\Logs), not
  a single file - see docs/policies-and-procedures/container-stack-operations.md.

  This is a Docker-Desktop-specific patch. On a Linux host running Docker Engine as a systemd
  service the socket is local and never proxied, so this whole class of problem disappears
  (tracked as the "evaluate Docker Engine runtime" follow-up under WX-7).

  Idempotent: if autoheal is already running it does nothing (no needless bounce on an ordinary
  logon), so this is safe to fire on every logon. Only a not-running autoheal (the post-reboot
  Exited(127) case, or a never-deployed one) is (re)created.

.PARAMETER ComposeFile
  Path to services/docker-compose.yml. Points at the repo copy (its build contexts + sibling
  appsettings are relative to that location, so it cannot be copied elsewhere like the backup
  script does). Default matches the canonical checkout (WX-241).

.PARAMETER Service
  Compose service name to reconcile. Default 'autoheal'.

.PARAMETER DockerReadyTimeoutSec
  How long to wait for `docker info` to succeed before giving up. Default 300s (5 min) - a
  congested boot can take several minutes to bring the engine up.
#>
[CmdletBinding()]
param(
  [string]$ComposeFile = 'C:\Code\HarderWare\services\docker-compose.yml',
  [string]$Service = 'autoheal',
  [int]$DockerReadyTimeoutSec = 300,
  # The registered task passes the InstallRoot-resolved dir explicitly (Register-AutohealTask.ps1). Left
  # empty here it is resolved below from InstallRoot so a manual run still logs beside the service logs.
  [string]$LogDir = ''
)

$ErrorActionPreference = 'Stop'

# Honor a non-default InstallRoot (WxServices\appsettings.shared.json) so the reconcile log sits with the
# other service logs, exactly as Deploy-WxService.ps1 resolves it. The compose file lives at
# <repoRoot>\services\docker-compose.yml, so its grandparent is the repo root that holds WxServices\.
if (-not $LogDir) {
  $installRoot = 'C:\HarderWare'
  $sharedConfig = Join-Path (Split-Path -Parent (Split-Path -Parent $ComposeFile)) 'WxServices\appsettings.shared.json'
  if (Test-Path $sharedConfig) {
    # Fail closed on a malformed/unreadable shared config: let it throw under the script-wide 'Stop'
    # (as Deploy-WxService.ps1 does) rather than silently reverting to the default root and writing the
    # reconcile log to the wrong {InstallRoot}\Logs tree, which would mask a real config problem.
    $sc = Get-Content $sharedConfig -Raw | ConvertFrom-Json
    if ($sc.InstallRoot) { $installRoot = $sc.InstallRoot }
  }
  $LogDir = Join-Path $installRoot 'Logs'
}

if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
$LogFile = Join-Path $LogDir 'autoheal-reconcile.log'

function Write-Log {
  param([string]$Level, [string]$Message)
  # Shared HarderWare PS-tool log format (matches Backup-WeatherData.ps1): literal '-' date separator
  # (culture-independent), level padded to 5, no brackets - so entries correlate with the other logs.
  $ts = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss.fff')
  $line = '{0} {1,-5} {2}' -f $ts, $Level, $Message
  try { Add-Content -Path $LogFile -Value $line } catch { Write-Warning "log write failed: $($_.Exception.Message)" }
  Write-Host $line
}

Write-Log 'INFO' "Autoheal reconcile start: service=$Service compose=$ComposeFile"

if (-not (Test-Path $ComposeFile)) {
  Write-Log 'ERROR' "Compose file not found: $ComposeFile - nothing to reconcile."
  exit 1
}

# From here on we drive native `docker` calls whose success/failure we read from $LASTEXITCODE, not
# from thrown errors. Drop out of the script-wide 'Stop': docker compose v2 writes its NORMAL progress
# ("Container ... Recreated/Started") to STDERR, and under 'Stop' a 2>&1 merge turns that first stderr
# line into a terminating NativeCommandError (PS 5.x) - which would abort a SUCCESSFUL recreate before
# we ever check the exit code. Every docker outcome below is checked explicitly via $LASTEXITCODE.
$ErrorActionPreference = 'Continue'

# 1) Wait for the Docker engine. On a fresh logon Docker Desktop is still starting; `docker info`
#    returns non-zero (or errors) until the engine is reachable. Poll until ready or timeout.
Write-Log 'INFO' "Waiting up to ${DockerReadyTimeoutSec}s for the Docker engine..."
$deadline = (Get-Date).AddSeconds($DockerReadyTimeoutSec)
$dockerReady = $false
while ((Get-Date) -lt $deadline) {
  docker info *> $null
  if ($LASTEXITCODE -eq 0) { $dockerReady = $true; break }
  Start-Sleep -Seconds 10
}
if (-not $dockerReady) {
  Write-Log 'ERROR' "Docker engine did not become ready within ${DockerReadyTimeoutSec}s. Giving up (the logon task will try again next logon)."
  exit 1
}
Write-Log 'INFO' 'Docker engine is ready.'

# 2) Skip if autoheal is already running - a stale mount always leaves it EXITED, never Running,
#    so a running container is genuinely healthy and force-recreating it would only open a brief
#    gap in healing coverage. Reconcile only the not-running (post-reboot Exited, or absent) case.
#    All docker calls pass the absolute -f $ComposeFile, so no working-directory change is needed.
#    -aq (not -q) so a STOPPED/Exited container is found too - the post-reboot failure leaves autoheal
#    Exited(127), which `ps -q` (running-only) would miss, mislabelling it "absent" in the log.
$id = (docker compose -f $ComposeFile ps -aq $Service 2>$null | Select-Object -First 1)
$running = $false
if ($id) {
  $running = ((docker inspect -f '{{.State.Running}}' $id 2>$null) -eq 'true')
}
if ($running) {
  Write-Log 'INFO' "autoheal already running ($id) - nothing to reconcile."
  exit 0
}

$state = if ($id) { (docker inspect -f '{{.State.Status}} exit={{.State.ExitCode}}' $id 2>$null) } else { 'absent' }
Write-Log 'INFO' "autoheal not running (state: $state) - force-recreating..."

docker compose -f $ComposeFile up -d --force-recreate $Service 2>&1 | ForEach-Object { Write-Log 'INFO' "compose: $_" }
if ($LASTEXITCODE -ne 0) {
  Write-Log 'ERROR' "docker compose up --force-recreate $Service failed (exit $LASTEXITCODE)."
  exit 1
}

# 3) Verify the new instance is actually running.
$newId = (docker compose -f $ComposeFile ps -aq $Service 2>$null | Select-Object -First 1)
if ($newId -and (docker inspect -f '{{.State.Running}}' $newId 2>$null) -eq 'true') {
  Write-Log 'INFO' "autoheal reconciled and running ($newId)."
  exit 0
}

$why = if ($newId) { (docker inspect -f '{{.State.Status}} exit={{.State.ExitCode}} err={{.State.Error}}' $newId 2>$null) } else { 'no container' }
Write-Log 'ERROR' "autoheal did NOT reach running after recreate (state: $why)."
exit 1
