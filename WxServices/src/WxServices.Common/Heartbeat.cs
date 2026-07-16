using WxServices.Logging;

namespace WxServices.Common;

/// <summary>
/// Writes a worker's heartbeat file: the single UTC timestamp WxMonitor and the container healthcheck
/// read to confirm the worker's loop is still turning. One shared writer (WX-68 Unit 2) replaces the
/// two hand-copied <c>WriteHeartbeat</c> methods that lived in <c>FetchWorker</c> and
/// <c>ReportWorker</c>, so every worker stamps the file identically.
/// </summary>
public static class Heartbeat
{
    /// <summary>
    /// Overwrites <paramref name="path"/> with the current UTC time in round-trip ("o") format — the
    /// format WxMonitor's <c>HeartbeatChecker</c> parses back when checking staleness. A
    /// null/whitespace path is a no-op (no heartbeat configured for this caller); a write failure is
    /// logged and swallowed, because a heartbeat must never take down the worker whose liveness it
    /// reports.
    /// </summary>
    /// <param name="path">Absolute path to the heartbeat file, or <see langword="null"/> to skip.</param>
    /// <sideeffects>Creates or overwrites the file at <paramref name="path"/> with an ISO 8601 UTC timestamp.</sideeffects>
    public static void Write(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Logger.Warn($"Could not write heartbeat to '{path}': {ex.Message}"); }
    }
}