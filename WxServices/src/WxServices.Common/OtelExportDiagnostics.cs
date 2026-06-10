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

    private readonly LogThrottle _exportFailureThrottle = new(ExportFailureRepeatWindow);

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

    /// <summary>True for the OTel SDK's own internal sources, which are all named <c>OpenTelemetry-*</c>.</summary>
    internal static bool IsOtelSource(string sourceName) =>
        sourceName.StartsWith("OpenTelemetry", StringComparison.Ordinal);

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
            // per window, so we never flood our own log over a multi-hour outage.
            if (!_exportFailureThrottle.ShouldLog(DateTime.UtcNow))
                return;
            Log.Warn($"{line}  (further export failures suppressed for {ExportFailureRepeatWindow.TotalMinutes:N0} min)");
            return;
        }

        // EventLevel ranks the OPPOSITE way from how we think of severity: it is ordered most-severe-first,
        // so LogAlways=0, Critical=1, Error=2, Warning=3, Informational=4, Verbose=5 — a LOWER number is MORE
        // severe. So "Error or worse" is `<= EventLevel.Error`, not `>=`. (We only ever enable Warning and
        // above, so anything not Error-or-worse here is a Warning.)
        if (e.Level <= EventLevel.Error)
            Log.Error(line);
        else
            Log.Warn(line);
    }
}