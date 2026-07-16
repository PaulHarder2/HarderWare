using WxServices.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Reads a heartbeat file written by a monitored worker and determines
/// whether the worker appears to be running.
/// <para>
/// The heartbeat file contains a single line: an ISO 8601 UTC timestamp
/// written by the worker at the end of every loop iteration (including
/// iterations that handle a fault without exiting).
/// </para>
/// </summary>
public static class HeartbeatChecker
{
    /// <summary>
    /// Returns the age of the most recent heartbeat relative to <paramref name="nowUtc"/>, or
    /// <see langword="null"/> if the file is missing or unreadable. The caller passes the monitor
    /// cycle's clock so heartbeat age, cooldown windows, and test time all key off one "now".
    /// </summary>
    public static TimeSpan? GetAge(string filePath, DateTime nowUtc)
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
                return nowUtc - ts.ToUniversalTime();
            }

            Logger.Warn($"Could not parse heartbeat timestamp from '{filePath}' — content: '{text}'.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read heartbeat file '{filePath}': {ex.Message}");
        }

        return null;
    }
}