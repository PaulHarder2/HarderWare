using WxParser.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Reads a heartbeat file written by a monitored service and determines
/// whether the service appears to be running.
/// <para>
/// The heartbeat file contains a single line: an ISO 8601 UTC timestamp
/// written by the service at the end of each successful cycle.
/// </para>
/// </summary>
public static class HeartbeatChecker
{
    /// <summary>
    /// Returns the age of the most recent heartbeat, or <see langword="null"/>
    /// if the file is missing or unreadable.
    /// </summary>
    public static TimeSpan? GetAge(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var text = File.ReadAllText(filePath).Trim();
            if (DateTime.TryParse(text, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var ts))
            {
                return DateTime.UtcNow - ts.ToUniversalTime();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read heartbeat file '{filePath}': {ex.Message}");
        }

        return null;
    }
}
