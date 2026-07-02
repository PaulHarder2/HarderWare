#requires -Version 5
<#
.SYNOPSIS
  Installs Backup-WeatherData.ps1 + its config into C:\HarderWare\backup and registers
  the "HarderWare WeatherData Backup" scheduled task (nightly full). WX-248.

.DESCRIPTION
  MUST be run elevated (administrator). Re-runnable (Register-ScheduledTask -Force).
  Mirrors the service-watchdog convention: SYSTEM principal, runtime scripts under
  C:\HarderWare. The task runs the installed copy so a repo checkout being mid-edit
  never affects the nightly run.

  NOTE: BACKUP runs server-side, so the SQL Server service account (e.g.
  'NT Service\MSSQL$SQLEXPRESS') must have write access to LocalStagingDir. If the first
  run logs an "Operating system error 5 (Access is denied)" on the .bak path, grant it:
    icacls "C:\HarderWare\backups" /grant "NT Service\MSSQL`$SQLEXPRESS:(OI)(CI)M"
#>
$ErrorActionPreference = 'Stop'

$InstallDir = 'C:\HarderWare\backup'
$LogDir = 'C:\HarderWare\Logs'
if (-not (Test-Path $LogDir)) { New-Item -ItemType Directory -Path $LogDir -Force | Out-Null }
try { Start-Transcript -Path (Join-Path $LogDir 'register-backup.out') -Force | Out-Null } catch {}

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) {
  Write-Warning "NOT elevated. Right-click PowerShell -> Run as administrator, then re-run this script."
  exit 1
}

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

# Resolve this script's own dir (guarded -- $PSScriptRoot is empty when dot-sourced/pasted).
$here = if ($PSScriptRoot) { $PSScriptRoot }
        elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path }
        else { (Get-Location).Path }

# Install the backup script (always refresh) + config (never clobber a curated one).
Copy-Item -Path (Join-Path $here 'Backup-WeatherData.ps1') -Destination $InstallDir -Force
$cfgDest = Join-Path $InstallDir 'Backup-Config.json'
if (Test-Path $cfgDest) {
  Write-Output "Kept existing config (not overwritten): $cfgDest"
} else {
  Copy-Item -Path (Join-Path $here 'Backup-Config.json') -Destination $cfgDest -Force
  Write-Output "Installed default config -> $cfgDest  (edit destinations/retention here)."
}

$script = Join-Path $InstallDir 'Backup-WeatherData.ps1'
$taskName = 'HarderWare WeatherData Backup'

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
  -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Type Full"

# Nightly full at 08:00. The trigger fires at LOCAL time; this box runs in UTC (WX design), so
# 08:00 local == 08:00 UTC. Warn if that ever stops being true so the schedule doesn't silently drift.
$hostTz = [TimeZoneInfo]::Local
if ($hostTz.GetUtcOffset((Get-Date)) -ne [TimeSpan]::Zero -or $hostTz.SupportsDaylightSavingTime) {
  Write-Warning ("Host time zone is not pure UTC (id '{0}', current offset {1}, DST={2}); the daily trigger fires at 08:00 LOCAL, which may not equal 08:00 UTC -- adjust -At or set the host to UTC." -f $hostTz.Id, $hostTz.GetUtcOffset((Get-Date)), $hostTz.SupportsDaylightSavingTime)
}
$trigger = New-ScheduledTaskTrigger -Daily -At '08:00'

$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' `
  -LogonType ServiceAccount -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries -StartWhenAvailable `
  -ExecutionTimeLimit (New-TimeSpan -Minutes 30)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
  -Principal $principal -Settings $settings -Force `
  -Description 'Nightly full backup of the WeatherData database (SIMPLE recovery) with verified offsite copy + retention prune. WX-248.' | Out-Null

Write-Output "Registered task: $taskName (daily 08:00 UTC)"
Get-ScheduledTask -TaskName $taskName | Format-List TaskName, State

# Differential task -- registered only when Backup-Config.json enables it (so the config's
# Differential block is not a dead setting). Disabled by default; MVP is full-only.
$cfg = try { Get-Content -Raw -Path $cfgDest | ConvertFrom-Json } catch { $null }
$diffName = 'HarderWare WeatherData Backup (Diff)'
if ($cfg -and $cfg.Differential -and $cfg.Differential.Enabled) {
  $hours = if ([int]$cfg.Differential.EveryHours -gt 0) { [int]$cfg.Differential.EveryHours } else { 6 }
  $diffAction = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Type Diff"
  $diffTrigger = New-ScheduledTaskTrigger -Once -At '00:30' `
    -RepetitionInterval (New-TimeSpan -Hours $hours) -RepetitionDuration ([TimeSpan]::MaxValue)
  Register-ScheduledTask -TaskName $diffName -Action $diffAction -Trigger $diffTrigger `
    -Principal $principal -Settings $settings -Force `
    -Description "Intra-day differential backup of WeatherData every $hours h (SIMPLE recovery). WX-248." | Out-Null
  Write-Output "Registered task: $diffName (every ${hours}h)"
} else {
  if (Get-ScheduledTask -TaskName $diffName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $diffName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "Removed stale differential task."
  }
  Write-Output "Differential backups disabled -- no diff task."
}

Write-Output ""
Write-Output "Test it now:  Start-ScheduledTask -TaskName '$taskName'"
Write-Output "Then check:   C:\HarderWare\Logs\weatherdata-backup.log"
try { Stop-Transcript | Out-Null } catch {}
