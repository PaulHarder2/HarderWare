#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publishes and redeploys one or all WxServices Windows services.

.PARAMETER ServiceName
    The service or application to deploy, or 'all' to deploy everything:
    the four Windows services (WxParserSvc, WxReportSvc, WxMonitorSvc, WxVisSvc),
    then WxManager, WxViewer, and WxVis cache clear.

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

$SolutionRoot = "C:\Users\PaulH\Dropbox\PH\Documents\Code\HarderWare\WxServices"

$ServiceMap = [ordered]@{
    'WxParserSvc'  = 'WxParser.Svc'
    'WxReportSvc'  = 'WxReport.Svc'
    'WxMonitorSvc' = 'WxMonitor.Svc'
    'WxVisSvc'     = 'WxVis.Svc'
}

# ---------------------------------------------------------------------------
# Ensure home location is configured in appsettings.shared.json.
# Only called for WxParserSvc; skipped if already set.
# ---------------------------------------------------------------------------
function Invoke-HomeLocationSetup {
    $sharedConfig = "$SolutionRoot\appsettings.shared.json"
    $config = Get-Content $sharedConfig -Raw | ConvertFrom-Json

    if ($config.Fetch.HomeIcao) { return }

    Write-Host "Home location is not configured."
    $resolved = $false

    while (-not $resolved) {
        $address = Read-Host "Enter your home address (e.g. '7323 Saddle Tree Drive, Spring, TX 77379')"
        if ([string]::IsNullOrWhiteSpace($address)) { continue }

        Write-Host "Validating address..."
        $encoded = [Uri]::EscapeDataString($address)
        $nomUrl  = "https://nominatim.openstreetmap.org/search?q=$encoded&format=json&addressdetails=1&limit=1"
        try {
            $results = Invoke-RestMethod -Uri $nomUrl -Headers @{ 'User-Agent' = 'WxServices/1.0' }
        } catch {
            Write-Warning "Nominatim request failed: $_"
            continue
        }

        if (-not $results -or $results.Count -eq 0) {
            Write-Warning "Address not found. Please try again."
            continue
        }

        $locality = if     ($results[0].address.suburb)  { $results[0].address.suburb  }
                    elseif ($results[0].address.town)     { $results[0].address.town    }
                    elseif ($results[0].address.village)  { $results[0].address.village }
                    elseif ($results[0].address.city)     { $results[0].address.city    }
                    elseif ($results[0].address.county)   { $results[0].address.county  }
                    else                                  { '(unknown locality)'         }

        $homeLat = [double]$results[0].lat
        $homeLon = [double]$results[0].lon
        Write-Host "Resolved to: $locality (lat=$homeLat, lon=$homeLon)"
        $confirm = Read-Host "Is this correct? (Y/N)"
        if ($confirm -notmatch '^[Yy]') { continue }

        Write-Host "Finding nearest METAR station..."
        $deg      = 2
        $bbox     = "$($homeLat - $deg),$($homeLon - $deg),$($homeLat + $deg),$($homeLon + $deg)"
        $metarUrl = "https://aviationweather.gov/api/data/metar?bbox=$bbox&hours=1&format=json"
        try {
            $stations = Invoke-RestMethod -Uri $metarUrl -Headers @{ 'User-Agent' = 'WxServices/1.0' }
        } catch {
            Write-Warning "METAR station lookup failed: $_"
            $stations = @()
        }

        $nearestIcao = $null
        if ($stations -and $stations.Count -gt 0) {
            $nearest     = $stations |
                Sort-Object { [Math]::Pow($_.lat - $homeLat, 2) + [Math]::Pow($_.lon - $homeLon, 2) } |
                Select-Object -First 1
            $nearestIcao = $nearest.icaoId
            Write-Host "Nearest METAR station: $nearestIcao"
        } else {
            Write-Warning "Could not find a nearby METAR station automatically."
        }

        $icaoInput = Read-Host "Press Enter to use '$nearestIcao', or type a different ICAO"
        $homeIcao  = if ([string]::IsNullOrWhiteSpace($icaoInput)) { $nearestIcao } else { $icaoInput.Trim().ToUpper() }

        $config.Fetch.HomeIcao      = $homeIcao
        $config.Fetch.HomeLatitude  = $homeLat
        $config.Fetch.HomeLongitude = $homeLon
        $config | ConvertTo-Json -Depth 5 | Set-Content $sharedConfig -Encoding UTF8
        Write-Host "Saved: HomeIcao='$homeIcao', lat=$homeLat, lon=$homeLon"
        $resolved = $true
    }
}

# ---------------------------------------------------------------------------
# Stop, publish, and start a single service.
# ---------------------------------------------------------------------------
function Invoke-ServiceDeploy {
    param([string]$SvcName)

    $projectFolder = $ServiceMap[$SvcName]
    $projectPath   = "$SolutionRoot\src\$projectFolder"
    $publishDir    = "C:\HarderWare\BuildCache\WxServices\$projectFolder\bin\Release\net8.0\publish"
    $binPath       = "$publishDir\$projectFolder.exe"

    if ($SvcName -eq 'WxParserSvc') {
        Invoke-HomeLocationSetup
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
# Publish WxManager WPF GUI to C:\HarderWare\WxManager
# ---------------------------------------------------------------------------
function Invoke-ManagerPublish {
    $projectPath = "$SolutionRoot\src\WxManager"
    $outputDir   = "C:\HarderWare\WxManager"

    Write-Host "Publishing WxManager to $outputDir..."
    dotnet publish $projectPath -c Release -o $outputDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for WxManager (exit code $LASTEXITCODE)."
        return $false
    }

    # dotnet publish does not reliably copy Content files for WPF projects; copy explicitly.
    foreach ($configFile in @('log4net.config', 'appsettings.local.json')) {
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
# Clear the WxVis Python bytecode cache so the next map render picks up
# any script changes without redeploying WxVis.Svc.
# ---------------------------------------------------------------------------
function Invoke-WxVisCacheClear {
    $cacheDir = "$SolutionRoot\src\WxVis\__pycache__"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force
        Write-Host "Cleared WxVis __pycache__." -ForegroundColor Green
    } else {
        Write-Host "WxVis __pycache__ not present — nothing to clear." -ForegroundColor Yellow
    }
    return $true
}

# ---------------------------------------------------------------------------
# Publish WxViewer WPF desktop app to C:\HarderWare\WxViewer
# ---------------------------------------------------------------------------
function Invoke-ViewerPublish {
    $projectPath = "$SolutionRoot\src\WxViewer"
    $outputDir   = "C:\HarderWare\WxViewer"

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
    $ok = Invoke-WxVisCacheClear
    exit $(if ($ok) { 0 } else { 1 })
}

if ($ServiceName -eq 'all') {
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
    Write-Host "=== WxVis ===" -ForegroundColor Cyan
    Invoke-WxVisCacheClear | Out-Null

    Write-Host ""
    Write-Host "All services and applications deployed." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "=== $ServiceName ===" -ForegroundColor Cyan
    Invoke-ServiceDeploy -SvcName $ServiceName
}
