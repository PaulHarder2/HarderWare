#requires -Version 5.1
<#
.SYNOPSIS
  Sample localized report output (or vocabulary) from the WeatherData database
  as UTF-8 - CORRECTLY, without the Windows-1252 codepage fold that silently
  strips non-Latin-1 diacritics (e.g. Esperanto c-circumflex U+0109, u-breve
  U+016D) in sqlcmd / console output while leaving German umlauts intact.

.DESCRIPTION
  Root cause of the fold this tool avoids: sqlcmd's console/file output, plain
  PowerShell redirection, and SSMS "results to text" render through a non-UTF-8
  codepage (often Windows-1252). That codepage KEEPS characters it contains
  (ae/oe/ue) and best-fit-FOLDS ones it lacks to their base letter (c-circumflex
  -> c). Result: a real, correct Esperanto report ("j/ceux with hats") looks
  ASCII-folded ("jaudo"), manufacturing a phantom defect. It was exactly this
  that produced a false "Esperanto lost its diacritics" report (WX-258, cleared
  2026-07-05 as a test error).

  This script sidesteps the trap end to end:
    * reads nvarchar columns via .NET SqlClient into UTF-16 strings (lossless);
    * writes each record to a UTF-8 (NO BOM) file - the authoritative artifact;
    * sets the console to UTF-8 so any printed excerpt renders;
    * NEVER routes text through sqlcmd or an ANSI/1252 encoding.

  For each record it prints a diacritic-integrity summary: length, count of
  non-ASCII characters, and the distinct non-ASCII code points present (as
  U+XXXX). Seeing U+0135 in the inventory is positive proof the diacritic
  survived. A non-English record with ZERO non-ASCII characters is flagged for
  a second look.

.PARAMETER RecipientId
  CommittedSends.RecipientId to filter on, e.g. paul_eo.

.PARAMETER Id
  A specific CommittedSends.Id (overrides RecipientId / Latest).

.PARAMETER Latest
  Number of most-recent records to sample (default 3).

.PARAMETER Column
  Which text column to sample: EmailBody (default), StructuredReport, ReasoningTrace.
  NOTE: StructuredReport is JSON with non-ASCII escaped as \uXXXX, so its raw
  text is ASCII by design - sample EmailBody to see rendered diacritics.

.PARAMETER Vocabulary
  Sample LanguageTemplates for -IsoCode instead of report bodies.

.PARAMETER IsoCode
  Language ISO code (e.g. eo) - required with -Vocabulary.

.PARAMETER OutDir
  Directory for the UTF-8 output files (default: $env:TEMP\wx-samples).

.EXAMPLE
  .\Sample-ReportOutput.ps1 -RecipientId paul_eo -Latest 2

.EXAMPLE
  .\Sample-ReportOutput.ps1 -Id 3957 -Column EmailBody

.EXAMPLE
  .\Sample-ReportOutput.ps1 -Vocabulary -IsoCode eo
#>
[CmdletBinding()]
param(
  [string]$RecipientId,
  [int]$Id,
  [int]$Latest = 3,
  [ValidateSet('EmailBody','StructuredReport','ReasoningTrace')]
  [string]$Column = 'EmailBody',
  [switch]$Vocabulary,
  [string]$IsoCode,
  [string]$OutDir = (Join-Path $env:TEMP 'wx-samples'),
  [string]$Server = '.\SQLEXPRESS',
  [string]$Database = 'WeatherData'
)

$ErrorActionPreference = 'Stop'

# Print UTF-8 so any excerpt renders. The UTF-8 file is authoritative regardless
# of the console font.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-NonAsciiInventory([string]$text) {
  $set = New-Object 'System.Collections.Generic.SortedSet[int]'
  foreach ($ch in $text.ToCharArray()) {
    $code = [int]$ch
    if ($code -gt 127) { [void]$set.Add($code) }
  }
  return $set
}

function Format-CodePoints($set) {
  if ($set.Count -eq 0) { return '(none)' }
  return (($set | ForEach-Object { 'U+{0:X4}' -f $_ }) -join ' ')
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$cs = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;"
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()
try {
  $cmd = $conn.CreateCommand()

  if ($Vocabulary) {
    if (-not $IsoCode) { throw "Provide -IsoCode with -Vocabulary (e.g. -IsoCode eo)." }
    $cmd.CommandText = @"
SELECT lt.Token, lt.Phrase, lt.ContextInfo, lt.Note
FROM LanguageTemplates lt
JOIN Languages l ON l.Id = lt.LanguageId
WHERE l.IsoCode = @iso
ORDER BY lt.Token;
"@
    [void]$cmd.Parameters.AddWithValue('@iso', $IsoCode)
    $source = 'LanguageTemplates'
  }
  else {
    if ($Id -gt 0) {
      $cmd.CommandText = "SELECT Id, RecipientId, CreatedAtUtc, [$Column] AS Body FROM CommittedSends WHERE Id = @id"
      [void]$cmd.Parameters.AddWithValue('@id', $Id)
    }
    elseif ($RecipientId) {
      $cmd.CommandText = "SELECT TOP ($Latest) Id, RecipientId, CreatedAtUtc, [$Column] AS Body FROM CommittedSends WHERE RecipientId = @rid ORDER BY Id DESC"
      [void]$cmd.Parameters.AddWithValue('@rid', $RecipientId)
    }
    else {
      $cmd.CommandText = "SELECT TOP ($Latest) Id, RecipientId, CreatedAtUtc, [$Column] AS Body FROM CommittedSends ORDER BY Id DESC"
    }
    $source = "CommittedSends.$Column"
  }

  Write-Host ""
  Write-Host ("Sampling {0} from {1} / {2}" -f $source, $Server, $Database)
  Write-Host ("Output dir: {0}" -f $OutDir)
  Write-Host ("-" * 72)

  $reader = $cmd.ExecuteReader()
  $n = 0
  while ($reader.Read()) {
    $n++
    if ($Vocabulary) {
      $token   = [string]$reader['Token']
      $phrase  = if ($reader['Phrase']      -is [DBNull]) { '' } else { [string]$reader['Phrase'] }
      $context = if ($reader['ContextInfo'] -is [DBNull]) { '' } else { [string]$reader['ContextInfo'] }
      $note    = if ($reader['Note']        -is [DBNull]) { '' } else { [string]$reader['Note'] }
      $text  = "Token: $token`r`nPhrase: $phrase`r`nContextInfo: $context`r`nNote: $note`r`n"
      $label = "$IsoCode-$token"
      $isEnglishish = ($IsoCode -eq 'en')
    }
    else {
      $recId = $reader['Id']
      $rid   = [string]$reader['RecipientId']
      $when  = ([datetime]$reader['CreatedAtUtc']).ToString('yyyy-MM-ddTHH:mm:ss')
      $text  = if ($reader['Body'] -is [DBNull]) { '' } else { [string]$reader['Body'] }
      $label = "$recId-$rid"
      $isEnglishish = ($rid -match '_en$')
    }

    $inv = Get-NonAsciiInventory $text
    $nonAscii = 0
    foreach ($c in $text.ToCharArray()) { if ([int]$c -gt 127) { $nonAscii++ } }

    $safe = ($label -replace '[^A-Za-z0-9_.-]', '_')
    $file = Join-Path $OutDir ("$safe.txt")
    [System.IO.File]::WriteAllText($file, $text, $utf8NoBom)

    Write-Host ("[{0}] {1}" -f $n, $label)
    if (-not $Vocabulary) { Write-Host ("    created : {0} UTC" -f $when) }
    Write-Host ("    length  : {0} chars, {1} non-ASCII" -f $text.Length, $nonAscii)
    Write-Host ("    codepts : {0}" -f (Format-CodePoints $inv))
    if ($nonAscii -eq 0 -and -not $isEnglishish) {
      Write-Host "    NOTE    : zero non-ASCII in a non-English record - verify (possible upstream fold, or simply none in this sample)."
    }
    Write-Host ("    file    : {0}" -f $file)
    Write-Host ""
  }
  $reader.Close()
  if ($n -eq 0) { Write-Host "No matching records." }
  else { Write-Host ("Wrote {0} UTF-8 file(s). Open them in any UTF-8-aware editor to see the text as stored." -f $n) }
}
finally { $conn.Close() }
