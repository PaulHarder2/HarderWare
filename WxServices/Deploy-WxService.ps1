#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes and redeploys one or all WxServices Windows services.

.DESCRIPTION
    Developer deployment script.  Reads InstallRoot from appsettings.shared.json
    (default C:\HarderWare) and derives all output paths from it.  The solution
    root is the directory containing this script ($PSScriptRoot).

.PARAMETER ServiceName
    The service or application to deploy, or 'all' to deploy everything:
    WxVis Python scripts first, then the four Windows services
    (WxParserSvc, WxReportSvc, WxMonitorSvc, WxVisSvc), WxManager, and WxViewer.

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
    [string]$ServiceName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Resolve paths ─────────────────────────────────────────────────────────────

$SolutionRoot = $PSScriptRoot

# Read InstallRoot from appsettings.shared.json (default: C:\HarderWare).
$sharedConfigPath = "$SolutionRoot\appsettings.shared.json"
$InstallRoot = "C:\HarderWare"
if (Test-Path $sharedConfigPath) {
    $sharedConfig = Get-Content $sharedConfigPath -Raw | ConvertFrom-Json
    if ($sharedConfig.InstallRoot) {
        $InstallRoot = $sharedConfig.InstallRoot
    }
}

Write-Host "Solution root: $SolutionRoot"
Write-Host "Install root:  $InstallRoot"
Write-Host ""

$ServiceMap = [ordered]@{
    'WxParserSvc'  = 'WxParser.Svc'
    'WxReportSvc'  = 'WxReport.Svc'
    'WxMonitorSvc' = 'WxMonitor.Svc'
    'WxVisSvc'     = 'WxVis.Svc'
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

    # Warn if home location is not yet configured (first-time setup).
    if ($SvcName -eq 'WxParserSvc' -and $sharedConfig -and -not $sharedConfig.Fetch.HomeIcao) {
        Write-Warning "Fetch:HomeIcao is not configured in appsettings.shared.json. Services will not fetch data until a home location is set."
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
            Write-Warning "$SvcName did not stop within ${timeout}s — aborting deploy of this service."
            return $false
        }
    }

    # Publish
    Write-Host "Publishing $projectFolder..."
    dotnet publish $projectPath -c Release
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

    # Start
    Write-Host "Starting $SvcName..."
    sc.exe start $SvcName | Out-Null
    Write-Host "$SvcName deployed and started." -ForegroundColor Green
    return $true
}

# ---------------------------------------------------------------------------
# Publish WxManager WPF GUI.
# ---------------------------------------------------------------------------
function Invoke-ManagerPublish {
    $projectPath = "$SolutionRoot\src\WxManager"
    $outputDir   = "$InstallRoot\WxManager"

    Write-Host "Publishing WxManager to $outputDir..."
    dotnet publish $projectPath -c Release -o $outputDir
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
    return $true
}

# ---------------------------------------------------------------------------
# Publish WxViewer WPF desktop app.
# ---------------------------------------------------------------------------
function Invoke-ViewerPublish {
    $projectPath = "$SolutionRoot\src\WxViewer"
    $outputDir   = "$InstallRoot\WxViewer"

    Write-Host "Publishing WxViewer to $outputDir..."
    dotnet publish $projectPath -c Release -o $outputDir
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

    Write-Host ""
    Write-Host "=== WxManager ===" -ForegroundColor Cyan
    if (-not (Invoke-ManagerPublish)) { exit 1 }

    Write-Host ""
    Write-Host "=== WxViewer ===" -ForegroundColor Cyan
    if (-not (Invoke-ViewerPublish)) { exit 1 }

    Write-Host ""
    Write-Host "All services and applications deployed." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "=== $ServiceName ===" -ForegroundColor Cyan
    Invoke-ServiceDeploy -SvcName $ServiceName
}
