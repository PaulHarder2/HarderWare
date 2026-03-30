#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or uninstalls the WxParser Windows service.

.DESCRIPTION
    Run with -Action Install to publish the project, copy it to the install
    directory, and register the Windows service (but not start it).
    Run with -Action Uninstall to stop and remove the service.
    The script must be run from an elevated (Administrator) PowerShell session.

.PARAMETER Action
    'Install' or 'Uninstall'. Default: Install.

.PARAMETER InstallDir
    Directory to publish the service into. Default: C:\Services\WxParser.

.PARAMETER ProjectRoot
    Root of the WxParser solution. Defaults to two levels above this script
    (i.e. the solution root when the script lives in src\WxParser.Svc\).

.EXAMPLE
    .\Install-Service.ps1
    .\Install-Service.ps1 -Action Uninstall
    .\Install-Service.ps1 -InstallDir "D:\Services\WxParser"
#>
param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action     = 'Install',

    [string]$InstallDir = 'C:\Services\WxParser',

    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ServiceName        = 'WxParserSvc'
$ServiceDisplayName = 'WxParser Weather Fetcher'
$ServiceDescription = 'Periodically fetches METAR and TAF reports and stores them in SQL Server.'
$ProjectPath        = Join-Path $ProjectRoot 'src\WxParser.Svc\WxParser.Svc.csproj'
$ExePath            = Join-Path $InstallDir  'WxParser.Svc.exe'

function Install-WxParserService {
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Host "Service '$ServiceName' already exists. Run with -Action Uninstall first." -ForegroundColor Yellow
        return
    }

    # Publish
    Write-Host "Publishing WxParser.Svc to '$InstallDir'..."
    dotnet publish $ProjectPath -c Release -o $InstallDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed (exit code $LASTEXITCODE)."
        exit 1
    }

    if (-not (Test-Path $ExePath)) {
        Write-Error "Expected executable not found after publish: $ExePath"
        exit 1
    }

    # Register
    Write-Host "Registering service '$ServiceName'..."
    sc.exe create $ServiceName binPath= "`"$ExePath`"" start= demand DisplayName= $ServiceDisplayName | Out-Null
    sc.exe description $ServiceName $ServiceDescription | Out-Null

    Write-Host "Service '$ServiceName' installed successfully." -ForegroundColor Green
    Write-Host "Review '$InstallDir\appsettings.json', then start with:  sc.exe start $ServiceName"
}

function Uninstall-WxParserService {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "Service '$ServiceName' is not installed." -ForegroundColor Yellow
        return
    }

    if ($svc.Status -ne 'Stopped') {
        Write-Host "Stopping service '$ServiceName'..."
        sc.exe stop $ServiceName | Out-Null
        Start-Sleep -Seconds 3
    }

    Write-Host "Removing service '$ServiceName'..."
    sc.exe delete $ServiceName | Out-Null

    # sc.exe delete marks the service for deletion; wait until the SCM releases it.
    $timeout = 30
    $elapsed = 0
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and $elapsed -lt $timeout) {
        Start-Sleep -Seconds 1
        $elapsed++
    }

    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Write-Warning "Service '$ServiceName' is still present after ${timeout}s. Close Services (services.msc) or any tool holding a handle to it, then try again."
    } else {
        Write-Host "Service '$ServiceName' removed." -ForegroundColor Green
    }
}

switch ($Action) {
    'Install'   { Install-WxParserService }
    'Uninstall' { Uninstall-WxParserService }
}
