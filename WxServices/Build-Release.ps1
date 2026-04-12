<#
.SYNOPSIS
    Publishes all WxServices components into a clean staging directory
    suitable for packaging with Inno Setup.

.DESCRIPTION
    Produces a release\ directory with the product layout:

        release\
        ├── appsettings.shared.json
        ├── log4net.shared.config
        ├── services\
        │   ├── WxParser.Svc\
        │   ├── WxReport.Svc\
        │   ├── WxMonitor.Svc\
        │   └── WxVis.Svc\
        ├── WxManager\
        ├── WxViewer\
        ├── WxVis\
        └── observability\

    Run this script from the solution root (the directory containing
    WxServices.sln).  The release\ directory is recreated on each run.

.EXAMPLE
    .\Build-Release.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SolutionRoot = $PSScriptRoot
$ReleaseDir   = "$SolutionRoot\release"

# ── Clean ─────────────────────────────────────────────────────────────────────

if (Test-Path $ReleaseDir) {
    Write-Host "Cleaning previous release..."
    Remove-Item $ReleaseDir -Recurse -Force
}

New-Item -ItemType Directory -Path $ReleaseDir | Out-Null

# ── Publish services ──────────────────────────────────────────────────────────

$services = @(
    @{ Name = 'WxParser.Svc';  Project = 'src\WxParser.Svc\WxParser.Svc.csproj' }
    @{ Name = 'WxReport.Svc';  Project = 'src\WxReport.Svc\WxReport.Svc.csproj' }
    @{ Name = 'WxMonitor.Svc'; Project = 'src\WxMonitor.Svc\WxMonitor.Svc.csproj' }
    @{ Name = 'WxVis.Svc';     Project = 'src\WxVis.Svc\WxVis.Svc.csproj' }
)

foreach ($svc in $services) {
    $outDir = "$ReleaseDir\services\$($svc.Name)"
    Write-Host "Publishing $($svc.Name)..."
    dotnet publish "$SolutionRoot\$($svc.Project)" -c Release -o $outDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for $($svc.Name)."
        exit 1
    }
}

# ── Publish WxManager ─────────────────────────────────────────────────────────

Write-Host "Publishing WxManager..."
dotnet publish "$SolutionRoot\src\WxManager\WxManager.csproj" -c Release -o "$ReleaseDir\WxManager" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed for WxManager."; exit 1 }

# ── Publish WxViewer ──────────────────────────────────────────────────────────

Write-Host "Publishing WxViewer..."
dotnet publish "$SolutionRoot\src\WxViewer\WxViewer.csproj" -c Release -o "$ReleaseDir\WxViewer" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed for WxViewer."; exit 1 }

# ── Copy Python scripts ──────────────────────────────────────────────────────

Write-Host "Copying WxVis Python scripts..."
$wxVisTarget = "$ReleaseDir\WxVis"
New-Item -ItemType Directory -Path $wxVisTarget | Out-Null
foreach ($pattern in @('*.py', 'requirements.txt')) {
    Copy-Item "$SolutionRoot\src\WxVis\$pattern" $wxVisTarget -Force -ErrorAction SilentlyContinue
}

# ── Copy shared config files ─────────────────────────────────────────────────

Write-Host "Copying shared configuration files..."
Copy-Item "$SolutionRoot\appsettings.shared.json" $ReleaseDir
Copy-Item "$SolutionRoot\log4net.shared.config"   $ReleaseDir

# ── Copy observability stack ──────────────────────────────────────────────────

Write-Host "Copying observability stack..."
$obsSource = "$SolutionRoot\..\observability"
if (Test-Path $obsSource) {
    Copy-Item $obsSource "$ReleaseDir\observability" -Recurse
}

# ── Copy bundled tools (wgrib2) ──────────────────────────────────────────────

Write-Host "Copying bundled tools..."
$toolsSource = "$SolutionRoot\tools"
if (Test-Path $toolsSource) {
    Copy-Item $toolsSource "$ReleaseDir\tools" -Recurse
}

# ── Copy documentation ───────────────────────────────────────────────────────

Write-Host "Copying documentation..."
foreach ($doc in @('INSTALL.md', 'DESIGN.md', 'PRE-INSTALL.txt')) {
    $src = "$SolutionRoot\$doc"
    if (Test-Path $src) { Copy-Item $src $ReleaseDir }
}

# ── Create empty directories ─────────────────────────────────────────────────

foreach ($dir in @('Logs', 'plots', 'temp')) {
    New-Item -ItemType Directory -Path "$ReleaseDir\$dir" -Force | Out-Null
}

# ── Read product version from Directory.Build.props ──────────────────────────

$propsPath = "$SolutionRoot\Directory.Build.props"
$productVersion = "0.0.0"
if (Test-Path $propsPath) {
    [xml]$props = Get-Content $propsPath
    $v = $props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }
    if ($v) { $productVersion = $v }
}

# ── Done ──────────────────────────────────────────────────────────────────────

$itemCount = (Get-ChildItem $ReleaseDir -Recurse -File).Count
Write-Host ""
Write-Host "Release staged: $ReleaseDir ($itemCount files) — WxServices $productVersion" -ForegroundColor Green
Write-Host "Next: compile the Inno Setup script to produce the installer:"
Write-Host "  ISCC /DAppVer=$productVersion HarderWare_WxServices.iss" -ForegroundColor Cyan

# ── Reminders ────────────────────────────────────────────────────────────────

$versionFile = "$SolutionRoot\tools\wgrib2-version.txt"
if (Test-Path $versionFile) {
    $bundled = Get-Content $versionFile -TotalCount 1
    Write-Host ""
    Write-Host "REMINDER: Bundled $bundled" -ForegroundColor Yellow
    Write-Host "  Check for a newer version: https://www.cpc.ncep.noaa.gov/products/wesley/wgrib2/" -ForegroundColor Yellow
    Write-Host "  To update: replace tools\wgrib2 and edit tools\wgrib2-version.txt" -ForegroundColor Yellow
}
