#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes/redeploys the WxServices: the WxParser/WxVis/WxReport/WxMonitor Docker
    containers, WxManager, and WxViewer.

.DESCRIPTION
    Developer deployment script.  Reads InstallRoot from appsettings.shared.json
    (default C:\HarderWare) and derives all output paths from it.  The solution
    root is the directory containing this script ($PSScriptRoot).

.PARAMETER ServiceName
    The service or application to deploy, or 'all' to deploy everything:
    the WxParserSvc, WxVisSvc, WxReportSvc, and WxMonitorSvc Docker containers
    (services/docker-compose.yml), the autoheal sidecar, then WxManager and WxViewer.
    'autoheal' brings up just the sidecar.

.EXAMPLE
    .\Deploy-WxService.ps1 WxReportSvc
    .\Deploy-WxService.ps1 all
    .\Deploy-WxService.ps1 autoheal
    .\Deploy-WxService.ps1 WxViewer
    .\Deploy-WxService.ps1 WxManager
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('WxParserSvc', 'WxReportSvc', 'WxMonitorSvc', 'WxVisSvc', 'WxViewer', 'WxManager', 'autoheal', 'all')]
    [string]$ServiceName,

    # Seconds to wait for a containerized service to log "Application started" before FAIL.
    # Aligns with the DB startup-retry budget (~5 min, Database:StartupRetry); raise for a slow cold start.
    [ValidateRange(1, [int]::MaxValue)]
    [int]$StartupTimeoutSec = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# -- Resolve paths -------------------------------------------------------------

$SolutionRoot = $PSScriptRoot

# Read InstallRoot from appsettings.shared.json (default: C:\HarderWare).
$sharedConfigPath = "$SolutionRoot\appsettings.shared.json"
$InstallRoot = "C:\HarderWare"
$sharedConfig = $null   # may stay null on a binaries-only host with no shared config; readers must guard
if (Test-Path $sharedConfigPath) {
    $sharedConfig = Get-Content $sharedConfigPath -Raw | ConvertFrom-Json
    if ($sharedConfig.InstallRoot) {
        $InstallRoot = $sharedConfig.InstallRoot
    }
}

Write-Host "Solution root: $SolutionRoot"
Write-Host "Install root:  $InstallRoot"
Write-Host ""

# All four headless services (WxParser/WxVis/WxReport/WxMonitor) deploy as Docker containers via
# Invoke-ContainerDeploy (WX-63..66); there are no Windows-service deploys left in this script.

# ---------------------------------------------------------------------------
# Deploy-history log + build identity (version from Directory.Build.props,
# git short SHA). One line is appended per successfully deployed app.
# ---------------------------------------------------------------------------
$DeployLog = "$InstallRoot\Logs\deploy-history.log"

$DeployVersion = 'unknown'
$propsPath = "$SolutionRoot\Directory.Build.props"
if (Test-Path $propsPath) {
    $m = Select-String -Path $propsPath -Pattern '<Version>(.*?)</Version>' | Select-Object -First 1
    if ($m) { $DeployVersion = $m.Matches[0].Groups[1].Value }
}

$DeployCommit = 'nogit'
try {
    $sha = (& git -C $SolutionRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sha) { $DeployCommit = "$sha".Trim() }
} catch {
    # Non-fatal: a binaries-only host has no git. Leave $DeployCommit = 'nogit',
    # but record why under -Verbose so deploy troubleshooting isn't guesswork.
    Write-Verbose "git rev-parse failed in '$SolutionRoot' ($($_.Exception.Message)); deploy-history commit stays 'nogit'."
}

# Append one line per deployed app, matching the services' log4net pattern
# (%utcdate{yyyy-MM-dd HH:mm:ss.fff} %-5level %message) so deploy-history.log
# sorts and reads alongside the wx*-svc.log files. Best-effort: a logging
# failure never aborts a deploy.
function Write-DeployLog {
    param([string]$App, [string]$Result = 'OK')
    try {
        $logDir = Split-Path $DeployLog -Parent
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
        $ts = [DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss.fff')
        $level = if ($Result -eq 'OK') { 'INFO' } else { 'ERROR' }
        $line = '{0} {1,-5} [Deploy] {2,-13} {3,-8} {4,-8} {5}' -f $ts, $level, $App, $DeployVersion, $DeployCommit, $Result
        # Append BOM-less UTF-8: Add-Content -Encoding UTF8 emits a BOM on Windows
        # PowerShell 5.x, which would prefix the first line and break the timestamp
        # column alignment with the BOM-less log4net wx*-svc.log files.
        [System.IO.File]::AppendAllText($DeployLog, $line + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    } catch {
        Write-Warning "Could not write deploy-history log ($DeployLog): $($_.Exception.Message)"
    }
}

# Warn (non-fatally) if WxParser's home location isn't configured yet - it won't fetch until it is.
# (This lived inside the old Invoke-ServiceDeploy; kept when WX-66 moved WxParser to a container and
# retired the Windows-service deploy path entirely.)
function Show-HomeIcaoWarningIfUnset {
    if (-not $sharedConfig) { return }
    $homeIcao = $null
    # Read the nested value inside try/catch: under StrictMode, indexing a missing JSON property
    # (a shared config with no Fetch node) throws, and we want a warning here, not a terminating error.
    try { $homeIcao = $sharedConfig.Fetch.HomeIcao } catch {
        Write-Verbose "Could not read Fetch.HomeIcao from shared config ($($_.Exception.Message))."
    }
    if (-not $homeIcao) {
        Write-Warning "Fetch:HomeIcao is not configured in appsettings.shared.json. WxParser will not fetch data until a home location is set."
    }
}

# ---------------------------------------------------------------------------
# Deploy a headless WxService as a Docker container (services/docker-compose.yml) instead of a
# Windows service - so (unlike the retired Windows-service path) there is no
# sc.exe service to stop/config/start, no publish-to-folder (the binary is baked into the image),
# and verification is docker-based (the Hosting.Lifetime "Application started" banner) rather than
# Get-Service. Deploy-history logging is identical - same Write-DeployLog, same component label,
# version, and git commit - so the verify scripts that grep deploy-history.log keep working.
#
# Generic over the compose service (WX-63 WxMonitor established it; WX-64 WxReport made it the
# second caller and motivated the extraction; WX-65/66 reuse it). $ComposeService is the
# docker-compose service name (e.g. 'wxmonitor', 'wxreport'); $DeployApp is the deploy-history
# component label (e.g. 'WxMonitorSvc').
# ---------------------------------------------------------------------------
function Invoke-ContainerDeploy {
    param(
        [Parameter(Mandatory)][string]$ComposeService,
        [Parameter(Mandatory)][string]$DeployApp
    )

    # services/ lives at the HarderWare repo root (a sibling of WxServices), NOT under $SolutionRoot
    # (which is the WxServices dir where this script sits). Resolve to the repo-root services/.
    $repoRoot    = Split-Path $SolutionRoot -Parent
    $composeDir  = "$repoRoot\services"
    $localConfig = "$composeDir\$ComposeService\appsettings.local.json"

    # Prereq: Docker Desktop reachable.
    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker is not reachable (is Docker Desktop running?). Cannot deploy the $ComposeService container."
        return $false
    }

    # Prereq: the real (gitignored) local config must exist. The compose bind mount
    # needs the source file; a missing source would be silently created as a directory
    # by Docker and fail confusingly inside the container.
    if (-not (Test-Path $localConfig)) {
        Write-Error "Missing $localConfig - copy appsettings.local.json.example, fill it in, then re-run."
        return $false
    }

    $started = $false
    Push-Location $composeDir
    try {
        # Build the image from current source and (re)create the container. | Out-Host
        # keeps docker output on the console but OUT of the success stream (PS 5.x: a
        # polluted stream turns the bool return into an always-truthy array).
        # Point the compose bind-mounts at the resolved host InstallRoot subdirs (forward slashes
        # for Docker Desktop) so a non-default InstallRoot still matches where native components look.
        # All WX_HOST_* are set for every service; each compose service uses only the ones it mounts
        # (wxmonitor: Logs; wxreport: Logs + plots + translation-qa).
        $env:WX_HOST_LOGS_DIR  = (Join-Path $InstallRoot 'Logs')           -replace '\\', '/'
        $env:WX_HOST_PLOTS_DIR = (Join-Path $InstallRoot 'plots')          -replace '\\', '/'
        $env:WX_HOST_QA_DIR    = (Join-Path $InstallRoot 'translation-qa') -replace '\\', '/'
        Write-Host "Building and starting the $ComposeService container..."
        docker compose up -d --build $ComposeService | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Error "docker compose up failed for $ComposeService (exit code $LASTEXITCODE)."
            return $false
        }

        # Verify: confirm the container we just (re)created reaches "Application started" AND is
        # still running. Pin to the NEW instance's ID so a crash-after-banner (or a stale prior
        # container's logs) can't be mistaken for success. The timeout matches the DB startup-retry
        # budget (Database:StartupRetry in appsettings.shared.json sums to ~5 min): a cold SQL Server
        # can legitimately delay EnsureSchemaAsync well past 30s, so a shorter deadline false-FAILs.
        $containerId = (docker compose ps -q $ComposeService 2>$null | Select-Object -First 1)
        Write-Host "Verifying the container reached 'Application started' (up to ${StartupTimeoutSec}s)..."
        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSec)
        while ($containerId -and [DateTime]::UtcNow -lt $deadline) {
            # Check this exact instance is still up first; a crashed container fails fast here.
            if ((docker inspect -f '{{.State.Running}}' $containerId 2>$null) -ne 'true') { break }
            $logs = (docker logs $containerId 2>&1) -join "`n"
            if ($logs -match 'Application started') { $started = $true; break }
            Start-Sleep -Seconds 3
        }
    } finally {
        Pop-Location
    }

    # Cutover: retire the native Windows service of the same name if one is still installed, so it
    # can't run beside the container (dual writers to the same DB + Logs/plots, the collision seen
    # when WxVis was first containerized). Done ONLY after the container is verified started, so a
    # failed container deploy leaves the native service as a fallback. Idempotent - a missing or
    # already-stopped/disabled service is a no-op. Requires elevation (#Requires -RunAsAdministrator).
    $cutoverOk = $true
    if ($started) {
        $native = Get-Service -Name $DeployApp -ErrorAction SilentlyContinue
        if ($native) {
            if ($native.Status -ne 'Stopped') {
                Write-Host "Stopping native $DeployApp service (superseded by the container)..."
                Stop-Service -Name $DeployApp -Force -ErrorAction SilentlyContinue
                # Stop-Service returns before the SCM finishes the stop; wait, then re-check, so we
                # never log a clean deploy while the old service is still writing beside the container.
                try { $native.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { <# timed out; the re-check below handles it #> }
                $native.Refresh()
                if ($native.Status -ne 'Stopped') {
                    Write-Warning "Native $DeployApp service did not stop within 30s - it is running beside the container (dual writers)."
                    $cutoverOk = $false
                }
            }
            if ($cutoverOk -and $native.StartType -ne 'Disabled') {
                Write-Host "Disabling native $DeployApp service (containerized; reversible fallback)..."
                Set-Service -Name $DeployApp -StartupType Disabled -ErrorAction SilentlyContinue
                $native.Refresh()
                if ($native.StartType -ne 'Disabled') {
                    Write-Warning "Could not disable native $DeployApp service - it may start again on reboot."
                    $cutoverOk = $false
                }
            }
        }
    }

    # Log OK only when BOTH the container verified AND (if a native counterpart existed) its cutover
    # completed - never report success while the old service could still run beside the container.
    $deployOk = $started -and $cutoverOk
    Write-DeployLog -App $DeployApp -Result $(if ($deployOk) { 'OK' } else { 'FAIL' })
    if ($deployOk) {
        Write-Host "$ComposeService container deployed and verified (Application started)." -ForegroundColor Green
    } elseif ($started) {
        Write-Warning "$ComposeService container is up, but the native $DeployApp service could not be retired; resolve the dual-run before relying on this deploy."
    } else {
        Write-Warning "$ComposeService container did not reach 'Application started' within ${StartupTimeoutSec}s. Check: docker compose logs $ComposeService (from $composeDir)."
    }
    return $deployOk
}

# ---------------------------------------------------------------------------
# Publish WxManager WPF GUI.
# ---------------------------------------------------------------------------
function Invoke-ManagerPublish {
    $projectPath = "$SolutionRoot\src\WxManager\WxManager.csproj"
    $outputDir   = "$InstallRoot\WxManager"

    Write-Host "Publishing WxManager to $outputDir..."
    dotnet publish $projectPath -c Release -o $outputDir | Out-Host   # | Out-Host: keeps build output off this function's bool return (PS 5.x stream-pollution guard)
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for WxManager (exit code $LASTEXITCODE)."
        return $false
    }

    # dotnet publish does not reliably copy Content files for WPF projects; copy explicitly.
    foreach ($configFile in @('appsettings.local.json')) {
        $src = "$projectPath\$configFile"
        if (Test-Path $src) {
            Copy-Item $src $outputDir
            Write-Host "Copied $configFile to $outputDir."
        }
    }

    Write-Host "WxManager published to $outputDir." -ForegroundColor Green
    Write-DeployLog -App 'WxManager'
    return $true
}

# ---------------------------------------------------------------------------
# Publish WxViewer WPF desktop app.
# ---------------------------------------------------------------------------
function Invoke-ViewerPublish {
    $projectPath = "$SolutionRoot\src\WxViewer\WxViewer.csproj"
    $outputDir   = "$InstallRoot\WxViewer"

    Write-Host "Publishing WxViewer to $outputDir..."
    dotnet publish $projectPath -c Release -o $outputDir | Out-Host   # | Out-Host: keeps build output off this function's bool return (PS 5.x stream-pollution guard)
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for WxViewer (exit code $LASTEXITCODE)."
        return $false
    }

    $sourceLocalConfig = "$projectPath\appsettings.local.json"
    if (Test-Path $sourceLocalConfig) {
        Copy-Item $sourceLocalConfig $outputDir
        Write-Host "Copied appsettings.local.json to publish dir."
    }

    Write-Host "WxViewer published to $outputDir." -ForegroundColor Green
    Write-DeployLog -App 'WxViewer'
    return $true
}

# ---------------------------------------------------------------------------
# Bring up the autoheal sidecar (WX-68). Unlike the four service containers it is NOT deployed by name
# via Invoke-ContainerDeploy: it has no "Application started" banner to verify, and nothing depends_on
# it, so a per-service 'compose up <service>' never creates it. A compose healthcheck only REPORTS
# health; the Docker restart policy fires on process EXIT, not on 'unhealthy'. This sidecar
# (willfarrell/autoheal) watches the autoheal=true-labelled service containers and restarts any that go
# unhealthy - the wedged-but-alive recovery plain compose does not provide. Started explicitly here (no
# --build; a pinned pulled image; no WX_HOST_* mounts - it only mounts the Docker socket) and verified
# running. restart:unless-stopped then carries it across reboots once started.
# ---------------------------------------------------------------------------
function Start-AutohealSidecar {
    $repoRoot   = Split-Path $SolutionRoot -Parent
    $composeDir = "$repoRoot\services"

    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        # -ErrorAction Continue: $ErrorActionPreference is 'Stop' script-wide, under which a bare
        # Write-Error terminates BEFORE the return, breaking the boolean contract (the 'all' caller
        # relies on $false to record [FAIL] + print the partial summary rather than abort).
        Write-Error "Docker is not reachable (is Docker Desktop running?). Cannot start the autoheal sidecar." -ErrorAction Continue
        return $false
    }

    $running = $false
    Push-Location $composeDir
    try {
        # | Out-Host keeps docker output off the success stream (PS 5.x: a polluted stream turns the
        # bool return into an always-truthy array).
        Write-Host "Starting the autoheal sidecar..."
        docker compose up -d autoheal | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Error "docker compose up failed for autoheal (exit code $LASTEXITCODE)." -ErrorAction Continue
            return $false
        }
        $id = (docker compose ps -q autoheal 2>$null | Select-Object -First 1)
        if ($id -and (docker inspect -f '{{.State.Running}}' $id 2>$null) -eq 'true') {
            $running = $true
        }
    } finally {
        Pop-Location
    }

    if ($running) {
        Write-Host "autoheal sidecar is running (restarts autoheal=true containers that go unhealthy)." -ForegroundColor Green
    } else {
        # Include -f <compose file>: this runs after Pop-Location, so a bare 'docker compose logs' would
        # miss the file from the caller's cwd.
        Write-Error "autoheal sidecar did not reach a running state. Check: docker compose -f $composeDir\docker-compose.yml logs autoheal" -ErrorAction Continue
    }
    return $running
}

# ---------------------------------------------------------------------------
# Consolidated end-of-run status table. The per-step [OK]/messages scroll off
# under each container's docker-build output, so this final summary is the
# at-a-glance "did everything come up?" - in particular whether each container
# deployed and (re)started. Printed at the end of an 'all' run (and before a
# non-zero exit on failure, so a partial run still reports how far it got).
# ---------------------------------------------------------------------------
function Show-DeploySummary {
    param([System.Collections.Generic.List[object]]$Results)

    Write-Host ""
    Write-Host "=== Deployment summary ===" -ForegroundColor Cyan
    foreach ($r in $Results) {
        $detail = switch ($r.Kind) {
            'container' { if ($r.Ok) { 'Application started' } else { 'did NOT start' } }
            'sidecar'   { if ($r.Ok) { 'running' }             else { 'did NOT start' } }
            default     { if ($r.Ok) { 'published' }           else { 'publish FAILED' } }
        }
        $tag = if ($r.Ok) { '[OK]  ' } else { '[FAIL]' }
        $line = '  {0} {1,-14} {2,-9} {3}' -f $tag, $r.Name, $r.Kind, $detail
        if (-not $r.Ok -and $r.Kind -eq 'container' -and $r.Compose) {
            $line += "  <- docker compose logs $($r.Compose)"
        }
        Write-Host $line -ForegroundColor $(if ($r.Ok) { 'Green' } else { 'Red' })
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
if ($ServiceName -eq 'WxViewer') {
    Invoke-ViewerPublish
    exit $LASTEXITCODE
}

if ($ServiceName -eq 'WxManager') {
    Invoke-ManagerPublish
    exit $LASTEXITCODE
}

if ($ServiceName -eq 'WxVisSvc') {
    $ok = Invoke-ContainerDeploy -ComposeService 'wxvis' -DeployApp 'WxVisSvc'
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'WxParserSvc') {
    Show-HomeIcaoWarningIfUnset
    $ok = Invoke-ContainerDeploy -ComposeService 'wxparser' -DeployApp 'WxParserSvc'
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'WxReportSvc') {
    $ok = Invoke-ContainerDeploy -ComposeService 'wxreport' -DeployApp 'WxReportSvc'
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'WxMonitorSvc') {
    $ok = Invoke-ContainerDeploy -ComposeService 'wxmonitor' -DeployApp 'WxMonitorSvc'
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'autoheal') {
    $ok = Start-AutohealSidecar
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'all') {
    # WxVis Python scripts are no longer copied to the host: WxVisSvc runs in a container (WX-65)
    # with the scripts baked into the image, and nothing on the host reads InstallRoot\WxVis anymore.
    #
    # Each step records its outcome into $results; on the first failure we stop (a broken dependency
    # must not deploy its dependents) but still print the summary of what was attempted before exiting.
    $results = New-Object System.Collections.Generic.List[object]
    $failed  = $false

    # WxParser (container) first - it populates the DB every other service reads.
    Show-HomeIcaoWarningIfUnset
    Write-Host ""
    Write-Host "=== WxParserSvc (container) ===" -ForegroundColor Cyan
    $ok = Invoke-ContainerDeploy -ComposeService 'wxparser' -DeployApp 'WxParserSvc'
    $results.Add([pscustomobject]@{ Name = 'WxParserSvc'; Kind = 'container'; Ok = $ok; Compose = 'wxparser' })
    if (-not $ok) { $failed = $true }

    # WxVis (container) - after WxParser (needs GFS/METAR data), before WxReport (WxVis renders the
    # meteogram/synoptic plots WxReport inlines).
    if (-not $failed) {
        Write-Host ""
        Write-Host "=== WxVisSvc (container) ===" -ForegroundColor Cyan
        $ok = Invoke-ContainerDeploy -ComposeService 'wxvis' -DeployApp 'WxVisSvc'
        $results.Add([pscustomobject]@{ Name = 'WxVisSvc'; Kind = 'container'; Ok = $ok; Compose = 'wxvis' })
        if (-not $ok) { $failed = $true }
    }

    # WxReport (container) - after the services that feed its DB + plots.
    if (-not $failed) {
        Write-Host ""
        Write-Host "=== WxReportSvc (container) ===" -ForegroundColor Cyan
        $ok = Invoke-ContainerDeploy -ComposeService 'wxreport' -DeployApp 'WxReportSvc'
        $results.Add([pscustomobject]@{ Name = 'WxReportSvc'; Kind = 'container'; Ok = $ok; Compose = 'wxreport' })
        if (-not $ok) { $failed = $true }
    }

    # WxMonitor (container) - last, after the services it watches.
    if (-not $failed) {
        Write-Host ""
        Write-Host "=== WxMonitorSvc (container) ===" -ForegroundColor Cyan
        $ok = Invoke-ContainerDeploy -ComposeService 'wxmonitor' -DeployApp 'WxMonitorSvc'
        $results.Add([pscustomobject]@{ Name = 'WxMonitorSvc'; Kind = 'container'; Ok = $ok; Compose = 'wxmonitor' })
        if (-not $ok) { $failed = $true }
    }

    # autoheal sidecar - after the service containers it watches. Not an Invoke-ContainerDeploy target
    # (no "Application started" banner, nothing depends_on it), so the per-service deploys never create
    # it; it needs this explicit bring-up (WX-68) or the healthchecks report health with nothing acting
    # on 'unhealthy'.
    if (-not $failed) {
        Write-Host ""
        Write-Host "=== autoheal sidecar ===" -ForegroundColor Cyan
        $ok = Start-AutohealSidecar
        $results.Add([pscustomobject]@{ Name = 'autoheal'; Kind = 'sidecar'; Ok = $ok; Compose = 'autoheal' })
        if (-not $ok) { $failed = $true }
    }

    # WxManager / WxViewer (WPF apps).
    if (-not $failed) {
        Write-Host ""
        Write-Host "=== WxManager ===" -ForegroundColor Cyan
        $ok = Invoke-ManagerPublish
        $results.Add([pscustomobject]@{ Name = 'WxManager'; Kind = 'app'; Ok = $ok; Compose = '' })
        if (-not $ok) { $failed = $true }
    }
    if (-not $failed) {
        Write-Host ""
        Write-Host "=== WxViewer ===" -ForegroundColor Cyan
        $ok = Invoke-ViewerPublish
        $results.Add([pscustomobject]@{ Name = 'WxViewer'; Kind = 'app'; Ok = $ok; Compose = '' })
        if (-not $ok) { $failed = $true }
    }

    Show-DeploySummary -Results $results
    if ($failed) {
        Write-Host ""
        Write-Warning "Deployment stopped at the first failure above; components after it were not attempted."
        exit 1
    }
    Write-Host ""
    Write-Host "All services and applications deployed." -ForegroundColor Green
    exit 0
}
