namespace WxServices.Common;

/// <summary>
/// The single canonical identity token for each WxServices background service. Every per-service
/// filename (log, heartbeat) derives from this one token, so the component that WRITES a file and the
/// monitor that READS it cannot resolve different names. The WX-106 heartbeat blind spot came from
/// exactly such a divergence: WxParser wrote <c>wxparser-heartbeat.txt</c> (token "wxparser") while the
/// monitor sought <c>wxparser-svc-heartbeat.txt</c> (its own <c>Name.Replace(".","-")</c> derivation
/// "wxparser-svc"). Routing both sides through this token removes the second source of truth (WX-290).
/// </summary>
public static class WxServiceToken
{
    /// <summary>WxParser fetch/parse service.</summary>
    public const string WxParser = "wxparser";

    /// <summary>WxReport reconcile/report service.</summary>
    public const string WxReport = "wxreport";

    /// <summary>WxVis visualization service.</summary>
    public const string WxVis = "wxvis";

    /// <summary>WxMonitor watchdog service.</summary>
    public const string WxMonitor = "wxmonitor";

    /// <summary>
    /// Resolves a monitor <c>WatchedServices[].Name</c> (e.g. "WxParser.Svc") to its canonical token.
    /// A registered service maps to its constant — the single source — so the monitor's resolved
    /// filenames are identical to the writer's; an unregistered name falls back to a normalized
    /// derivation (strip a trailing ".Svc", drop dots, lowercase) so a new watched entry still resolves
    /// sensibly rather than throwing. This is the seam the WX-290 anti-drift test pins.
    /// </summary>
    public static string FromConfigName(string configName) => configName.Trim() switch
    {
        "WxParser.Svc" => WxParser,
        "WxReport.Svc" => WxReport,
        "WxVis.Svc" => WxVis,
        "WxMonitor.Svc" => WxMonitor,
        var other => Normalize(other),
    };

    private static string Normalize(string name)
    {
        var token = name.EndsWith(".Svc", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return token.Replace(".", "", StringComparison.Ordinal).ToLowerInvariant();
    }
}