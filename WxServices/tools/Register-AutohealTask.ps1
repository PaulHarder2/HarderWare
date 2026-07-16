#requires -Version 5
<#
.SYNOPSIS
  Installs Reconcile-AutohealSidecar.ps1 into C:\HarderWare\autoheal and registers the
  "HarderWare Autoheal Reboot Reconcile" scheduled task (fires at logon). WX-68.

.DESCRIPTION
  MUST be run elevated (administrator). Re-runnable (Register-ScheduledTask -Force).

  WHY A LOGON TASK (not SYSTEM-at-startup like the backup task). Docker Desktop is a per-user
  tray app: its engine only comes up when the interactive user logs on, and the engine's named
  pipe lives in THAT user's session. A SYSTEM task at boot would race ahead of Docker Desktop's
  own start and would not share the user's Docker context, so it could never reach the engine.
  This task therefore runs AS the interactive user, AT logon (after Windows auto-login), and the
  reconcile script polls `docker info` until the engine is actually ready before acting.

  The reconcile script points at the repo compose file in place (its build contexts + sibling
  appsettings are relative to services/, so unlike the backup script's config it cannot be copied
  to C:\HarderWare). Only the reconcile SCRIPT is installed under C:\HarderWare so a repo checkout
  being mid-edit never affects the logon run.
#>
$ErrorActionPreference = 'Stop'

# Resolve this script's own dir (guarded -- $PSScriptRoot is empty when dot-sourced/pasted).
$here = if ($PSScriptRoot) { $PSScriptRoot }
        elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path }
        else { (Get-Location).Path }

# Honor a non-default InstallRoot the same way Deploy-WxService.ps1 does: read it from
# WxServices\appsettings.shared.json (tools -> WxServices), defaulting to C:\HarderWare. Both the install
# dir and the log dir hang off it, so a customized InstallRoot keeps this tooling beside the rest.
$solutionRoot = Split-Path -Parent $here
$sharedConfigPath = Join-Path $solutionRoot 'appsettings.shared.json'
$InstallRoot = 'C:\HarderWare'
if (Test-Path $sharedConfigPath) {
  $sharedConfig = Get-Content $sharedConfigPath -Raw | ConvertFrom-Json
  if ($sharedConfig.InstallRoot) { $InstallRoot = $sharedConfig.InstallRoot }
}

$InstallDir = Join-Path $InstallRoot 'autoheal'
$LogDir = Join-Path $InstallRoot 'Logs'
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
try { Start-Transcript -Path (Join-Path $LogDir 'register-autoheal.out') -Force | Out-Null }
catch { Write-Warning "Could not start registration transcript: $($_.Exception.Message)" }
Write-Output "Install root:  $InstallRoot"

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) {
  Write-Warning "NOT elevated. Right-click PowerShell -> Run as administrator, then re-run this script."
  exit 1
}

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

# Resolve the repo compose file: tools -> WxServices -> <repo root> -> services\docker-compose.yml.
$repoRoot = Split-Path -Parent $solutionRoot
$composePath = Join-Path $repoRoot 'services\docker-compose.yml'
if (-not (Test-Path $composePath)) {
  Write-Warning "Compose file not found at $composePath - the task will still install, but verify the path."
}

# Install the reconcile script (always refresh).
Copy-Item -Path (Join-Path $here 'Reconcile-AutohealSidecar.ps1') -Destination $InstallDir -Force
$script = Join-Path $InstallDir 'Reconcile-AutohealSidecar.ps1'
Write-Output "Installed reconcile script -> $script"

$taskName = 'HarderWare Autoheal Reboot Reconcile'
$who = "$env:USERDOMAIN\$env:USERNAME"

# Pass the InstallRoot-resolved compose file AND log dir so the boot run lands beside the service logs
# even under a non-default InstallRoot (the script would otherwise re-resolve LogDir from the compose path).
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
  -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -ComposeFile `"$composePath`" -LogDir `"$LogDir`""

# Fire at THIS user's logon, delayed 2 minutes so Docker Desktop's autostart has a head start
# (the script still polls `docker info`, so the delay is only to avoid a pointless first attempt).
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $who
$trigger.Delay = 'PT2M'

# Run AS the interactive user (Docker Desktop's engine pipe is session-bound), elevated so the log
# write to the InstallRoot log dir never trips an ACL. Interactive logon type = uses the live session that
# Windows auto-login provides after a reboot.
$principal = New-ScheduledTaskPrincipal -UserId $who `
  -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries -StartWhenAvailable `
  -ExecutionTimeLimit (New-TimeSpan -Minutes 15)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
  -Principal $principal -Settings $settings -Force `
  -Description 'Re-creates the autoheal sidecar after a host reboot so its Docker socket mount is re-minted (Docker Desktop leaves a single-FILE mount stale on autostart -> exit 127). Fires at logon; the script waits for the Docker engine, then force-recreates only if autoheal is not already running. WX-68.' | Out-Null

Write-Output "Registered task: $taskName (at logon of $who, +2m delay)"
Get-ScheduledTask -TaskName $taskName | Format-List TaskName, State

Write-Output ""
Write-Output "Test it now:  Start-ScheduledTask -TaskName '$taskName'"
Write-Output "Then check:   $(Join-Path $LogDir 'autoheal-reconcile.log')"
try { Stop-Transcript | Out-Null } catch {}
