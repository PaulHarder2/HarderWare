<#
.SYNOPSIS
    Scans the WxServices logs for serious errors in a recent time window.

.DESCRIPTION
    Reads the active service logs under C:\HarderWare\Logs (the *.log files,
    excluding rolled *.log.1/.2/... backups), filters to entries from the last
    N hours, and reports ERROR/FATAL entries and exception/traceback mentions --
    grouped and de-duplicated -- plus a per-file summary and the top recurring
    warnings. This replaces the ad-hoc grep/awk sweep with one repeatable,
    pre-approvable command.

    Log timestamps are UTC and the HarderWare host runs on UTC, so the window is
    computed against the local clock (Get-Date). Pass -Hours to widen/narrow it.

    Exit code is 0 when no ERROR/FATAL entries are found in the window and 1 when
    there are -- so it can drive a scheduled task or an alert.

.PARAMETER Hours
    Size of the look-back window in hours. Default 24.

.PARAMETER LogDir
    Directory holding the service logs. Default C:\HarderWare\Logs.

.PARAMETER TopWarnings
    How many recurring warning groups to list. Default 10. Set to 0 to skip.

.EXAMPLE
    .\Scan-WxLogs.ps1

.EXAMPLE
    .\Scan-WxLogs.ps1 -Hours 48 -TopWarnings 20
#>
[CmdletBinding()]
param(
    [int]$Hours = 24,
    [string]$LogDir = 'C:\HarderWare\Logs',
    [int]$TopWarnings = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cutoff  = (Get-Date).AddHours(-$Hours)
$tsRegex = '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+\s+(?<lvl>[A-Za-z]+)\s'

if (-not (Test-Path -LiteralPath $LogDir)) {
    Write-Error "Log directory not found: $LogDir"
    exit 2
}

# Active logs only: *.log skips rolled *.log.1/.2 and the *-heartbeat.txt files.
$logFiles = Get-ChildItem -LiteralPath $LogDir -Filter '*.log' -File | Sort-Object Name
if (-not $logFiles) {
    Write-Output "No *.log files found in $LogDir"
    exit 0
}

$fileSummaries = New-Object System.Collections.Generic.List[object]
$serious  = @{}   # key -> object: Count, Example, Last, File, Level
$warnings = @{}   # key -> object: Count, Example, Last, File

function Add-Group {
    param($Table, $Key, $Msg, $Ts, $File, $Level)
    if (-not $Table.ContainsKey($Key)) {
        $Table[$Key] = [pscustomobject]@{
            Count = 0; Example = $Msg; Last = $Ts; File = $File; Level = $Level
        }
    }
    $g = $Table[$Key]
    $g.Count++
    $g.Last = $Ts
}

foreach ($f in $logFiles) {
    $lines = Get-Content -LiteralPath $f.FullName -ReadCount 0 -ErrorAction SilentlyContinue
    if (-not $lines) { continue }

    $inWindow = $false
    $cE = 0; $cF = 0; $cW = 0; $cX = 0
    $lastTs = $null

    foreach ($line in $lines) {
        $m = [regex]::Match($line, $tsRegex)
        if (-not $m.Success) { continue }   # continuation / stack-trace lines

        $tsText = $m.Groups['ts'].Value
        try {
            $ts = [datetime]::ParseExact($tsText, 'yyyy-MM-dd HH:mm:ss', $null)
        } catch { continue }

        $inWindow = ($ts -ge $cutoff)
        if (-not $inWindow) { continue }

        $lastTs = $tsText
        $lvl    = $m.Groups['lvl'].Value.ToUpper()
        $msg    = $line.Substring($m.Length).Trim()
        $norm   = ($msg -replace '\d+', '#')
        $isExc  = ($line -match '(?i)exception|traceback')
        if ($isExc) { $cX++ }

        if ($lvl -eq 'ERROR' -or $lvl -eq 'FATAL') {
            if ($lvl -eq 'ERROR') { $cE++ } else { $cF++ }
            Add-Group -Table $serious -Key "$lvl|$norm" -Msg $msg -Ts $tsText -File $f.Name -Level $lvl
        }
        elseif ($lvl -eq 'WARN' -or $lvl -eq 'WARNING') {
            $cW++
            Add-Group -Table $warnings -Key "$norm" -Msg $msg -Ts $tsText -File $f.Name -Level $lvl
        }
    }

    $fileSummaries.Add([pscustomobject]@{
        File = $f.Name; ERROR = $cE; FATAL = $cF; WARN = $cW; EXC = $cX; Last = $lastTs
    })
}

# ---- Report ---------------------------------------------------------------
Write-Output "WxServices log scan"
Write-Output ("Generated : {0:yyyy-MM-dd HH:mm:ss}" -f (Get-Date))
Write-Output ("Window    : last {0}h (since {1:yyyy-MM-dd HH:mm:ss})" -f $Hours, $cutoff)
Write-Output ("Log dir   : {0}" -f $LogDir)
Write-Output ("Files     : {0}" -f $logFiles.Count)
Write-Output ""

Write-Output "Per-file summary (entries within window)"
Write-Output "---------------------------------------"
$fmt = "{0,-28} {1,6} {2,6} {3,6} {4,6}   {5}"
Write-Output ($fmt -f 'File', 'ERROR', 'FATAL', 'WARN', 'EXC', 'Last entry')
foreach ($fs in $fileSummaries) {
    $last = if ($fs.Last) { $fs.Last } else { '(none in window)' }
    Write-Output ($fmt -f $fs.File, $fs.ERROR, $fs.FATAL, $fs.WARN, $fs.EXC, $last)
}
Write-Output ""

$seriousList = @($serious.Values | Sort-Object Count -Descending)
if ($seriousList.Count -eq 0) {
    Write-Output "SERIOUS (ERROR/FATAL): none in window."
} else {
    Write-Output "SERIOUS (ERROR/FATAL) - grouped, most frequent first"
    Write-Output "---------------------------------------------------"
    foreach ($s in $seriousList) {
        Write-Output ("[{0}x] last {1} | {2} | {3}" -f $s.Count, $s.Last, $s.File, $s.Level)
        Write-Output ("      {0}" -f $s.Example)
    }
}
Write-Output ""

if ($TopWarnings -gt 0) {
    $warnList = @($warnings.Values | Sort-Object Count -Descending | Select-Object -First $TopWarnings)
    if ($warnList.Count -gt 0) {
        Write-Output ("Top recurring warnings (top {0})" -f $TopWarnings)
        Write-Output "-------------------------------"
        foreach ($w in $warnList) {
            Write-Output ("[{0}x] last {1} | {2}" -f $w.Count, $w.Last, $w.File)
            Write-Output ("      {0}" -f $w.Example)
        }
        Write-Output ""
    }
}

$sum = ($serious.Values | Measure-Object Count -Sum).Sum
$totalSerious = if ($sum) { [int]$sum } else { 0 }
if ($totalSerious -gt 0) {
    $word = if ($totalSerious -eq 1) { 'entry' } else { 'entries' }
    Write-Output ("VERDICT: {0} ERROR/FATAL {1} in the last {2}h - review needed." -f $totalSerious, $word, $Hours)
    exit 1
} else {
    Write-Output ("VERDICT: clean - no ERROR/FATAL entries in the last {0}h." -f $Hours)
    exit 0
}
