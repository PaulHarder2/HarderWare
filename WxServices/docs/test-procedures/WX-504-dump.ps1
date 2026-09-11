param([Parameter(Mandatory)][string]$OutPath, [Parameter(Mandatory)][string]$Since)
$ErrorActionPreference = 'Stop'
# WX-504 corpus dump: every SENT CommittedSend since -Since (UTC), joined to its anchored
# snapshot, the recipient's language and both timezone columns. UTF-8 JSON Lines, written through
# .NET so no console codepage can fold eo characters.
$sql = @"
SELECT cs.Id, cs.RecipientId, cs.SentAtUtc, cs.IsDiagnostic, cs.ForecastSnapshotId,
       cs.StructuredReport, cs.EmailBody, fs.Body AS SnapshotBody, fs.StationIcao,
       r.Timezone AS RecipientTz, loc.Timezone AS LocalityTz, lang.IsoCode
FROM CommittedSends cs
JOIN ForecastSnapshots fs ON fs.Id = cs.ForecastSnapshotId
LEFT JOIN Recipients r    ON r.RecipientId = cs.RecipientId
LEFT JOIN Localities loc  ON loc.Id = r.LocalityId
LEFT JOIN Languages lang  ON lang.Id = r.LanguageId
WHERE cs.SentAtUtc >= @since AND cs.SentAtUtc IS NOT NULL
ORDER BY cs.Id
"@
$cn = New-Object System.Data.SqlClient.SqlConnection 'Server=.\SQLEXPRESS;Database=WeatherData;Trusted_Connection=True;TrustServerCertificate=True;'
$cn.Open()
$cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
[void]$cmd.Parameters.AddWithValue('@since', [DateTime]::Parse($Since, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal))
$rd = $cmd.ExecuteReader()
$sb = New-Object System.Text.StringBuilder
$n = 0
while ($rd.Read()) {
    $row = [ordered]@{}
    for ($i = 0; $i -lt $rd.FieldCount; $i++) {
        $v = $rd.GetValue($i)
        if ($v -is [DBNull]) { $v = $null }
        elseif ($v -is [DateTime]) { $v = $v.ToString('yyyy-MM-ddTHH:mm:ssZ') }
        $row[$rd.GetName($i)] = $v
    }
    [void]$sb.AppendLine(($row | ConvertTo-Json -Compress -Depth 3))
    $n++
}
$rd.Close(); $cn.Close()
[System.IO.File]::WriteAllText($OutPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "rows=$n"
