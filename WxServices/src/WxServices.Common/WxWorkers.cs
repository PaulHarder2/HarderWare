namespace WxServices.Common;

/// <summary>
/// One background worker's heartbeat identity: the <see cref="Service"/> it runs in (a
/// <see cref="WxServiceToken"/> value, e.g. <c>"wxvis"</c>) and the <see cref="Worker"/> loop within
/// that service (e.g. <c>"analysis"</c>). The pair yields a per-worker <see cref="Token"/>
/// (<c>"wxvis-analysis"</c>) from which <see cref="WxPaths.HeartbeatFile(WxWorker)"/> derives the
/// file <c>wxvis-analysis-heartbeat.txt</c>. <see cref="DefaultMaxAgeMinutes"/> is the freshness
/// bound WxMonitor and the container healthcheck use to judge the worker stale — a generous multiple
/// of the worker's loop cadence, so an idle-but-alive loop never trips it.
/// </summary>
public sealed record WxWorker(string Service, string Worker, int DefaultMaxAgeMinutes)
{
    /// <summary>The per-worker token <c>"{Service}-{Worker}"</c> (e.g. <c>"wxvis-analysis"</c>).</summary>
    public string Token => $"{Service}-{Worker}";
}

/// <summary>
/// The single canonical registry of every WxServices background worker that emits a heartbeat.
/// Both sides derive from this one place, so a writer and a reader can never disagree about a
/// worker's heartbeat filename — the WX-106 blind-spot class (writer/monitor divergence), now closed
/// at the finer worker grain (WX-68 Unit 2). Each worker writes
/// <c>WxPaths.HeartbeatFile(itsWorker)</c>; WxMonitor watches <see cref="All"/> minus its own service
/// (a worker cannot report its own death); the compose healthchecks read the same names. Adding a
/// worker is one entry here. Note the split from <see cref="WxServiceToken"/>: the token stays the
/// per-<em>service</em> identity for logs (one <c>-svc.log</c> per process); this adds the
/// per-<em>worker</em> axis that only heartbeats need.
/// </summary>
public static class WxWorkers
{
    // Freshness bounds are generous multiples of each worker's loop cadence (WX-68 Unit 2 groom):
    // monitor/report cycle ~5 min, fetch ~10 min, gfs 60 min, qa polls every 10 s, and the three vis
    // loops tick every 30 s-1 min.
    public static readonly WxWorker Monitor = new(WxServiceToken.WxMonitor, "monitor", 15);
    public static readonly WxWorker ParserFetch = new(WxServiceToken.WxParser, "fetch", 30);
    public static readonly WxWorker ParserGfs = new(WxServiceToken.WxParser, "gfs", 90);
    public static readonly WxWorker ReportReport = new(WxServiceToken.WxReport, "report", 15);
    public static readonly WxWorker ReportQa = new(WxServiceToken.WxReport, "qa", 5);
    public static readonly WxWorker VisAnalysis = new(WxServiceToken.WxVis, "analysis", 5);
    public static readonly WxWorker VisForecast = new(WxServiceToken.WxVis, "forecast", 5);
    public static readonly WxWorker VisMeteogram = new(WxServiceToken.WxVis, "meteogram", 5);

    /// <summary>
    /// Every registered worker, grouped by service. Wrapped in <see cref="Array.AsReadOnly{T}"/> so it
    /// is genuinely immutable: a bare collection expression targeting <see cref="IReadOnlyList{T}"/>
    /// compiles to a <c>WxWorker[]</c> that a caller could cast back and mutate, whereas the
    /// <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/> this produces cannot be
    /// cast to a mutable array — the canonical set can't be tampered with at runtime.
    /// </summary>
    public static readonly IReadOnlyList<WxWorker> All = Array.AsReadOnly(new[]
    {
        Monitor,
        ParserFetch, ParserGfs,
        ReportReport, ReportQa,
        VisAnalysis, VisForecast, VisMeteogram,
    });
}