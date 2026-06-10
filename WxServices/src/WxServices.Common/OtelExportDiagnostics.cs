using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

using log4net;

namespace WxServices.Common;

/// <summary>
/// Bridges the OpenTelemetry SDK's internal <see cref="EventSource"/> diagnostics to log4net so that
/// silent failures — above all an unreachable OTLP collector — surface in the service log instead of
/// only as a gap in the dashboards. Motivated by the 2026-06-10 incident: after a reboot left the
/// Docker collector down, the OTLP exporter failed silently for ~8 hours and nothing in the log said so.
///
/// <para>
/// The exporter re-emits a failure event every export interval (~10 s) while the collector is down, so
/// exporter-source events are throttled to an onset plus a heartbeat (<see cref="ExportFailureRepeatWindow"/>)
/// — trading a silent blackout for a heartbeat, not a log flood. Process-global and idempotent:
/// <see cref="Enable"/> attaches a single listener for the lifetime of the process.
/// </para>
/// </summary>
internal sealed class OtelExportDiagnostics : EventListener
{
    /// <summary>How long repeat exporter-failure log lines are suppressed after one is emitted.</summary>
    internal static readonly TimeSpan ExportFailureRepeatWindow = TimeSpan.FromMinutes(5);

    private static readonly ILog Log = LogManager.GetLogger(typeof(OtelExportDiagnostics));
    private static readonly object Gate = new();
    private static OtelExportDiagnostics? _instance;

    // One throttle per distinct exporter event (source + id + name), so the SAME failure repeating
    // every ~10s collapses to an onset + heartbeat while a genuinely different exporter problem still
    // surfaces promptly. Keys are a small fixed set (the exporter EventSource's event ids), so this
    // stays tiny.
    private readonly ConcurrentDictionary<string, LogThrottle> _exporterThrottles = new();

    private OtelExportDiagnostics() { }

    /// <summary>
    /// Attaches the listener once for this process; its events are logged under this type's log4net
    /// category (the shared appenders). Safe to call repeatedly — only the first call creates the listener.
    /// </summary>
    public static void Enable()
    {
        lock (Gate)
        {
            _instance ??= new OtelExportDiagnostics();
        }
    }

    /// <summary>True for the OTel SDK's own internal sources, which are all named <c>OpenTelemetry-*</c>
    /// — the trailing hyphen keeps us off any unrelated source that merely starts with "OpenTelemetry".</summary>
    internal static bool IsOtelSource(string sourceName) =>
        sourceName.StartsWith("OpenTelemetry-", StringComparison.Ordinal);

    /// <summary>True for the OTLP/exporter sources, whose failure events repeat each export interval and are throttled.</summary>
    internal static bool IsExporterSource(string sourceName) =>
        sourceName.Contains("Exporter", StringComparison.Ordinal);

    /// <summary>Renders an event's format string against its payload, falling back to the event name.</summary>
    internal static string FormatMessage(string? message, IReadOnlyList<object?>? payload, string? eventName)
    {
        if (string.IsNullOrEmpty(message))
            return string.IsNullOrEmpty(eventName) ? "(unnamed event)" : eventName;
        if (payload is null || payload.Count == 0)
            return message;
        try { return string.Format(message, payload.ToArray()); }
        catch (FormatException) { return message; }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (IsOtelSource(source.Name))
            EnableEvents(source, EventLevel.Warning);   // Warning + Error
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        var sourceName = e.EventSource?.Name ?? "OpenTelemetry";
        var line = $"OTel[{sourceName}/{e.EventName}]: {FormatMessage(e.Message, e.Payload, e.EventName)}";

        if (IsExporterSource(sourceName))
        {
            // High-frequency while a collector is down: log the onset, then a heartbeat at most once
            // per window per distinct event, so we never flood the log (or WxMonitor's inbox) over a
            // multi-hour outage, yet a different exporter failure isn't masked by an ongoing one.
            var key = $"{sourceName}:{e.EventId}:{e.EventName}";
            var throttle = _exporterThrottles.GetOrAdd(key, _ => new LogThrottle(ExportFailureRepeatWindow));
            if (!throttle.ShouldLog(DateTime.UtcNow))
                return;
            line += $"  (further '{e.EventName}' events suppressed for {ExportFailureRepeatWindow.TotalMinutes:N0} min)";
        }

        // EventLevel ranks the OPPOSITE way from how we think of severity: it is ordered most-severe-first,
        // so LogAlways=0, Critical=1, Error=2, Warning=3, Informational=4, Verbose=5 — a LOWER number is MORE
        // severe. So "Error or worse" is `<= EventLevel.Error`, not `>=`. (We only enable Warning and above.)
        //
        // Logging at the SDK's own level is also what makes this reach a human: an unreachable collector is
        // emitted at Error, and WxMonitor alerts (emails) on ERROR+ — a WARN would only reach the log file,
        // which would defeat the point of WX-159 (knowing the telemetry pipeline broke).
        if (e.Level <= EventLevel.Error)
            Log.Error(line);
        else
            Log.Warn(line);
    }
}