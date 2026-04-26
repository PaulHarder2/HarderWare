using WxServices.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Parses a log4net rolling-file log and returns entries above a severity threshold
/// that are newer than a given timestamp.
/// <para>
/// Expected line format (from the %-5level PatternLayout):
///   <c>yyyy-MM-dd HH:mm:ss.fff LEVEL [File::Method:Line] message</c>
/// Exception stack traces appear on subsequent lines without a timestamp prefix
/// and are aggregated into the preceding entry.
/// </para>
/// </summary>
public static class LogScanner
{
    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEBUG"] = 0,
        ["INFO"] = 1,
        ["WARN"] = 2,
        ["ERROR"] = 3,
        ["FATAL"] = 4,
    };

    /// <summary>
    /// Scans <paramref name="filePath"/> and returns all log entries at or above
    /// <paramref name="minSeverity"/> whose timestamp is strictly after <paramref name="since"/>.
    /// <para>
    /// If <paramref name="since"/> is <see langword="null"/> (first run), returns an empty list
    /// but reports back the latest timestamp found so future scans have a baseline.
    /// </para>
    /// </summary>
    /// <param name="latestTimestamp">
    /// On return: the most recent timestamp seen in the file, or the incoming
    /// <paramref name="since"/> value if no new entries were found.
    /// </param>
    public static IReadOnlyList<LogEntry> Scan(
        string filePath,
        DateTime? since,
        string minSeverity,
        out DateTime? latestTimestamp)
    {
        latestTimestamp = since;

        if (!File.Exists(filePath))
            return [];

        var minRank = SeverityRank.GetValueOrDefault(minSeverity.Trim(), 3);
        var entries = ParseEntries(filePath);

        // Update the latest timestamp seen regardless of severity filter.
        if (entries.Count > 0)
            latestTimestamp = entries[^1].Timestamp;

        // On first run (since == null) just establish the baseline — no alerts.
        if (since is null)
            return [];

        return entries
            .Where(e => e.Timestamp > since.Value
                     && SeverityRank.GetValueOrDefault(e.Severity, 0) >= minRank)
            .ToList();
    }

    // ── parser ────────────────────────────────────────────────────────────────

    private static List<LogEntry> ParseEntries(string filePath)
    {
        var entries = new List<LogEntry>();

        List<string>? currentLines = null;
        DateTime currentTs = default;
        string currentSev = "";

        try
        {
            // Open with ReadWrite share so we don't conflict with log4net's MinimalLock.
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (TryParseHeader(line, out var ts, out var sev))
                {
                    // Flush previous entry.
                    if (currentLines is not null)
                        entries.Add(new LogEntry(currentTs, currentSev, string.Join(Environment.NewLine, currentLines)));

                    currentTs = ts;
                    currentSev = sev;
                    currentLines = [line];
                }
                else
                {
                    // Continuation line (exception / stack trace).
                    currentLines?.Add(line);
                }
            }

            // Flush final entry.
            if (currentLines is not null)
                entries.Add(new LogEntry(currentTs, currentSev, string.Join(Environment.NewLine, currentLines)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read log file '{filePath}': {ex.Message}");
        }

        return entries;
    }

    /// <summary>
    /// Returns true if <paramref name="line"/> starts with a valid log4net timestamp header.
    /// Format: <c>yyyy-MM-dd HH:mm:ss.fff LEVEL </c> (29+ chars).
    /// </summary>
    private static bool TryParseHeader(string line, out DateTime timestamp, out string severity)
    {
        timestamp = default;
        severity = "";

        // Minimum: 23 chars timestamp + 1 space + 5 severity = 29 chars.
        if (line.Length < 29) return false;

        if (!DateTime.TryParseExact(
                line[..23],
                "yyyy-MM-dd HH:mm:ss.fff",
                null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out timestamp))
            return false;

        severity = line.Substring(24, 5).Trim();
        return true;
    }
}

public record LogEntry(DateTime Timestamp, string Severity, string Text);