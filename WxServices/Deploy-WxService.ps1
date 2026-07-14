#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes/redeploys the WxServices: the WxParser/WxReport/WxVis Windows services,
    the WxMonitor Docker container, WxManager, WxViewer, and the WxVis Python scripts.

.DESCRIPTION
    Developer deployment script.  Reads InstallRoot from appsettings.shared.json
    (default C:\HarderWare) and derives all output paths from it.  The solution
    root is the directory containing this script ($PSScriptRoot).

.PARAMETER ServiceName
    The service or application to deploy, or 'all' to deploy everything:
    WxVis Python scripts first, then the three Windows services
    (WxParserSvc, WxReportSvc, WxVisSvc), then WxMonitorSvc as a Docker
    container (services/docker-compose.yml), then WxManager and WxViewer.

.EXAMPLE
    .\Deploy-WxService.ps1 WxReportSvc
    .\Deploy-WxService.ps1 all
    .\Deploy-WxService.ps1 WxViewer
    .\Deploy-WxService.ps1 WxManager
    .\Deploy-WxService.ps1 WxVis
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('WxParserSvc', 'WxReportSvc', 'WxMonitorSvc', 'WxVisSvc', 'WxViewer', 'WxManager', 'WxVis', 'all')]
    [string]$ServiceName,

    # Seconds to wait for the WxMonitor container to log "Application started" before FAIL.
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

# WxMonitorSvc is deployed as a Docker container (Invoke-MonitorContainerDeploy),
# not a Windows service, so it is intentionally absent from this Windows-service map.
$ServiceMap = [ordered]@{
    'WxParserSvc'  = 'WxParser.Svc'
    'WxReportSvc'  = 'WxReport.Svc'
    'WxVisSvc'     = 'WxVis.Svc'
}

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

# Poll the given Windows services until all are Running or the timeout elapses
# (one overall deadline; all services are checked each pass, so the wait is
# bounded by the slowest service, not the sum). Prints a per-service summary.
# Returns $true only if every service is Running.
function Test-ServicesRunning {
    param([string[]]$Services, [int]$TimeoutSeconds = 60)

    Write-Host ""
    Write-Host "Verifying services are running (timeout ${TimeoutSeconds}s)..." -ForegroundColor Cyan

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $pending = @($Services | Where-Object {
            $svc = Get-Service -Name $_ -ErrorAction SilentlyContinue
            (-not $svc) -or ($svc.Status -ne 'Running')
        })
        if ($pending.Count -eq 0) { break }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        Start-Sleep -Seconds 2
    }

    $allOk = $true
    foreach ($s in $Services) {
        $svc = Get-Service -Name $s -ErrorAction SilentlyContinue
        $status = if ($svc) { "$($svc.Status)" } else { 'NotInstalled' }
        if ($status -eq 'Running') {
            Write-Host ("  [OK]   {0}: {1}" -f $s, $status) -ForegroundColor Green
        } else {
            Write-Host ("  [FAIL] {0}: {1}" -f $s, $status) -ForegroundColor Red
            $allOk = $false
        }
    }
    return $allOk
}

# ---------------------------------------------------------------------------
# Stop, publish, and start a single service.
# ---------------------------------------------------------------------------
function Invoke-ServiceDeploy {
    param([string]$SvcName)

    $projectFolder = $ServiceMap[$SvcName]
    $projectPath   = "$SolutionRoot\src\$projectFolder"
    $publishDir    = "$InstallRoot\BuildCache\WxServices\$projectFolder\bin\Release\net8.0\publish"
    $binPath       = "$publishDir\$projectFolder.exe"

    # Warn if home location is not yet configured (first-time setup). Read the
    # nested value inside try/catch: under StrictMode, indexing a missing JSON
    # property (e.g. a shared config with no Fetch node) throws, and we want a
    # warning here, not a terminating error.
    if ($SvcName -eq 'WxParserSvc' -and $sharedConfig) {
        $homeIcao = $null
        try { $homeIcao = $sharedConfig.Fetch.HomeIcao } catch {
            # Non-fatal: shared config has no Fetch node. The "not configured"
            # warning below still fires; note the detail under -Verbose.
            Write-Verbose "Could not read Fetch.HomeIcao from shared config for ${SvcName} ($($_.Exception.Message))."
        }
        if (-not $homeIcao) {
            Write-Warning "Fetch:HomeIcao is not configured in appsettings.shared.json. Services will not fetch data until a home location is set."
        }
    }

    # Stop
    $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Write-Host "Stopping $SvcName..."
        sc.exe stop $SvcName | Out-Null

        $timeout = 30
        $elapsed = 0
        while ($elapsed -lt $timeout) {
            Start-Sleep -Seconds 1
            $elapsed++
            $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
            if (-not $svc -or $svc.Status -eq 'Stopped') { break }
        }
        if ($svc -and $svc.Status -ne 'Stopped') {
            Write-Warning "$SvcName did not stop within ${timeout}s - aborting deploy of this service."
            return $false
        }
    }

    # Publish
    Write-Host "Publishing $projectFolder..."
    # | Out-Host keeps the build output on the console but OUT of this function's
    # success stream; otherwise it pollutes the return value, making it a
    # multi-element array (always truthy), which defeats the callers' -not checks.
    dotnet publish $projectPath -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for $projectFolder (exit code $LASTEXITCODE)."
        return $false
    }

    if (-not (Test-Path $binPath)) {
        Write-Error "Expected executable not found after publish: $binPath"
        return $false
    }

    $sourceLocalConfig = "$projectPath\appsettings.local.json"
    if (Test-Path $sourceLocalConfig) {
        Copy-Item $sourceLocalConfig $publishDir
        Write-Host "Copied appsettings.local.json to publish dir."
    }

    # Update registered binary path in case it has changed (e.g. after build output was relocated)
    sc.exe config $SvcName binpath= "`"$binPath`"" | Out-Null

    # Start, then verify the service actually reaches Running before logging the
    # deploy. sc.exe start is asynchronous, so a service that starts but crashes
    # during init (e.g. the WX-113 SCM-30s migration timeout) would otherwise be
    # logged as a successful deploy. Test-ServicesRunning gates the log line.
    Write-Host "Starting $SvcName..."
    sc.exe start $SvcName | Out-Null

    $running = Test-ServicesRunning -Services @($SvcName)
    Write-DeployLog -App $SvcName -Result $(if ($running) { 'OK' } else { 'FAIL' })
    if ($running) {
        Write-Host "$SvcName deployed and verified Running." -ForegroundColor Green
    } else {
        Write-Warning "$SvcName was published and started but is not Running."
    }
    return $running
}

# ---------------------------------------------------------------------------
# Deploy WxMonitor as a Docker container (services/docker-compose.yml) instead of a
# Windows service. The Windows-service path (Invoke-ServiceDeploy) does not apply:
# there is no sc.exe service to stop/config/start, no publish-to-folder (the binary is
# baked into the image), and verification is docker-based (the Hosting.Lifetime
# "Application started" banner) rather than Get-Service. Deploy-history logging is
# identical - same Write-DeployLog, same 'WxMonitorSvc' component label, version, and
# git commit - so the verify scripts that grep deploy-history.log keep working.
# ---------------------------------------------------------------------------
function Invoke-MonitorContainerDeploy {
    $composeDir  = "$SolutionRoot\services"
    $localConfig = "$composeDir\wxmonitor\appsettings.local.json"

    # Prereq: Docker Desktop reachable.
    docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker is not reachable (is Docker Desktop running?). Cannot deploy the WxMonitor container."
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
        # Point the compose log bind-mount at the resolved host InstallRoot\Logs (forward slashes
        # for Docker Desktop) so a non-default InstallRoot still matches where native components look.
        $env:WX_HOST_LOGS_DIR = (Join-Path $InstallRoot 'Logs') -replace '\\', '/'
        Write-Host "Building and starting the wxmonitor container..."
        docker compose up -d --build wxmonitor | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Error "docker compose up failed for wxmonitor (exit code $LASTEXITCODE)."
            return $false
        }

        # Verify: confirm the container we just (re)created reaches "Application started" AND is
        # still running. Pin to the NEW instance's ID so a crash-after-banner (or a stale prior
        # container's logs) can't be mistaken for success. The timeout matches the DB startup-retry
        # budget (Database:StartupRetry in appsettings.shared.json sums to ~5 min): a cold SQL Server
        # can legitimately delay EnsureSchemaAsync well past 30s, so a shorter deadline false-FAILs.
        $containerId = (docker compose ps -q wxmonitor 2>$null | Select-Object -First 1)
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

    Write-DeployLog -App 'WxMonitorSvc' -Result $(if ($started) { 'OK' } else { 'FAIL' })
    if ($started) {
        Write-Host "wxmonitor container deployed and verified (Application started)." -ForegroundColor Green
    } else {
        Write-Warning "wxmonitor container did not reach 'Application started' within ${StartupTimeoutSec}s. Check: docker compose logs wxmonitor (from $composeDir)."
    }
    return $started
}

# ---------------------------------------------------------------------------
# Publish WxManager WPF GUI.
# ---------------------------------------------------------------------------
function Invoke-ManagerPublish {
    $projectPath = "$SolutionRoot\src\WxManager\WxManager.csproj"
    $outputDir   = "$InstallRoot\WxManager"

    Write-Host "Publishing WxManager to $outputDir..."
    dotnet publish $projectPath -c Release -o $outputDir | Out-Host   # | Out-Host: keep build output off the bool return (see Invoke-ServiceDeploy)
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
# Copy WxVis Python scripts to InstallRoot\WxVis and clear bytecode cache.
# ---------------------------------------------------------------------------
function Invoke-WxVisPublish {
    $sourceDir = "$SolutionRoot\src\WxVis"
    $targetDir = "$InstallRoot\WxVis"

    if (-not (Test-Path $sourceDir)) {
        Write-Error "WxVis source directory not found: $sourceDir"
        return $false
    }

    # Create target directory if needed.
    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir | Out-Null
    }

    # Copy Python scripts and supporting files.
    foreach ($pattern in @('*.py', 'requirements.txt')) {
        Copy-Item "$sourceDir\$pattern" $targetDir -Force -ErrorAction SilentlyContinue
    }
    Write-Host "Copied WxVis Python scripts to $targetDir."

    # Clear bytecode cache in target directory.
    $cacheDir = "$targetDir\__pycache__"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force
        Write-Host "Cleared WxVis __pycache__."
    }

    Write-Host "WxVis published to $targetDir." -ForegroundColor Green
    Write-DeployLog -App 'WxVis'
    return $true
}

# ---------------------------------------------------------------------------
# Publish WxViewer WPF desktop app.
# ---------------------------------------------------------------------------
function Invoke-ViewerPublish {
    $projectPath = "$SolutionRoot\src\WxViewer\WxViewer.csproj"
    $outputDir   = "$InstallRoot\WxViewer"

    Write-Host "Publishing WxViewer to $outputDir..."
    dotnet publish $projectPath -c Release -o $outputDir | Out-Host   # | Out-Host: keep build output off the bool return (see Invoke-ServiceDeploy)
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

if ($ServiceName -eq 'WxVis') {
    $ok = Invoke-WxVisPublish
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'WxMonitorSvc') {
    $ok = Invoke-MonitorContainerDeploy
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'all') {
    # Copy Python scripts first so WxVisSvc finds them immediately after restart.
    Write-Host ""
    Write-Host "=== WxVis ===" -ForegroundColor Cyan
    if (-not (Invoke-WxVisPublish)) { exit 1 }

    foreach ($target in $ServiceMap.Keys) {
        Write-Host ""
        Write-Host "=== $target ===" -ForegroundColor Cyan
        $ok = Invoke-ServiceDeploy -SvcName $target
        if (-not $ok) {
            Write-Warning "Stopping 'all' deploy due to failure in $target."
            exit 1
        }
    }

    # WxMonitor deploys as a container, after the Windows services it watches.
    Write-Host ""
    Write-Host "=== WxMonitorSvc (container) ===" -ForegroundColor Cyan
    if (-not (Invoke-MonitorContainerDeploy)) {
        Write-Warning "Stopping 'all' deploy due to failure in WxMonitorSvc."
        exit 1
    }

    Write-Host ""
    Write-Host "=== WxManager ===" -ForegroundColor Cyan
    if (-not (Invoke-ManagerPublish)) { exit 1 }

    Write-Host ""
    Write-Host "=== WxViewer ===" -ForegroundColor Cyan
    if (-not (Invoke-ViewerPublish)) { exit 1 }

    Write-Host ""
    Write-Host "All services and applications deployed." -ForegroundColor Green
    # No final consolidated health check: each service was already verified at its own deploy step
    # (Invoke-ServiceDeploy verifies Running; the container deploy verifies the "Application started"
    # banner) and any failure exits non-zero there, so a re-check would only repeat reported results.
} else {
    Write-Host ""
    Write-Host "=== $ServiceName ===" -ForegroundColor Cyan
    # Invoke-ServiceDeploy now verifies-then-logs internally; its return value is
    # the running status, so no separate Test-ServicesRunning call is needed here.
    if (-not (Invoke-ServiceDeploy -SvcName $ServiceName)) { exit 1 }
}
