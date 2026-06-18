// Configuration model for the WxReport.Svc service.
// Non-secret settings are populated from the "Report" section of appsettings files.
// Secrets (SMTP credentials, Claude API key) are stored in the GlobalSettings
// database row and loaded at runtime — they never appear in config files.

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// Root configuration model for WxReport.Svc.
/// <para>
/// Every property here is a <b>default</b>. At startup <c>ReportWorker.LoadConfigsAsync</c>
/// binds the <c>Report</c> section of <c>appsettings.shared.json</c> over a fresh instance
/// (<c>GetSection("Report").Bind(...)</c>), so any value present in that file <b>wins</b> and
/// these initializers apply only to keys the file omits. The shared file is the canonical
/// source of truth (it is required at startup; a missing or unparseable file fails the host
/// rather than silently using these defaults). Edit settings there, not here — these literals
/// exist so the service is sensible out of the box and so each setting is self-documenting.
/// </para>
/// </summary>
public class ReportConfig
{
    /// <summary>Language to use when no per-recipient language is specified (e.g. "English", "Spanish").</summary>
    public string DefaultLanguage { get; set; } = "English";

    /// <summary>Hour(s) of day (0–23, in the recipient's local timezone) at which the daily scheduled report is sent.  Comma-separated when multiple hours are desired (e.g. "6, 12").</summary>
    public string DefaultScheduledSendHours { get; set; } = "7";

    /// <summary>
    /// Minimum minutes that must elapse between any two reports sent to the same
    /// recipient.  Raised from 60 to 90 in WX-110.  As of WX-157/WX-181 this is the
    /// <em>hard floor of last resort</em>, not the primary limiter: the scheduled path
    /// is exempt (a due slot fires on time, WX-157), and the common significant
    /// non-severe unscheduled path is governed by the longer
    /// <see cref="UpdateDebounceSchedule"/> (post-send) and
    /// <see cref="PreScheduledQuietSchedule"/> (pre-slot) day-banded windows.  This
    /// floor still binds where neither does: (1) a not-severe→severe onset, which
    /// punches through both day-banded windows — the floor is then the only limiter
    /// preventing two severe sends inside 90 min; and (2) when the significance gate is
    /// Off or a day-banded schedule fails to parse (both day-banded windows live inside
    /// the gate stage).  Do not remove.
    /// </summary>
    public int MinGapMinutes { get; set; } = 90;

    /// <summary>
    /// WX-181 day-banded debounce for *unscheduled* updates: the minimum gap since the
    /// last unscheduled send as a step function of how far out (recipient-local days,
    /// 1 = today) the change reaches.  Default <c>"1:360,3:720"</c> — a change touching
    /// today/tomorrow needs 360 min (6 h), one that only reaches day 3+ needs 720 min
    /// (12 h).  Parsed by <see cref="DayBandedSchedule"/> (strict: first day must be 1,
    /// days ascending; value in minutes).  A not-severe→severe change punches through;
    /// <see cref="MinGapMinutes"/> remains a hard floor beneath this.
    /// </summary>
    public string UpdateDebounceSchedule { get; set; } = "1:360,3:720";

    /// <summary>
    /// WX-157 day-banded pre-scheduled quiet window: how long *before* the next
    /// scheduled slot a significant, non-severe-onset unscheduled update is held back —
    /// its content rides the upcoming scheduled report (which renders "What's changed")
    /// — as a step function of how far out (recipient-local days, 1 = today) the change
    /// reaches.  Default <c>"1:90,3:180"</c> — a near-term (today/tomorrow) change is
    /// quieted only in the 90 min before the slot, a day-3+ change in the 180 min
    /// before it (a far-out change can wait for the digest).  Parsed by
    /// <see cref="DayBandedSchedule"/> (strict; value in minutes).  A not-severe→severe
    /// onset punches through; <see cref="MinGapMinutes"/> remains a hard floor beneath.
    /// </summary>
    public string PreScheduledQuietSchedule { get; set; } = "1:90,3:180";

    /// <summary>Check interval; loaded from appsettings.json (service-specific).</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Path to the heartbeat file written after each successful report cycle. Read by WxMonitor.Svc.</summary>
    public string? HeartbeatFile { get; set; }

    /// <summary>
    /// Minimum precipitation rate in mm/hr for a GFS forecast hour to be counted
    /// as precipitation in the daily summary.  Hours below this threshold are
    /// treated as dry.  Defaults to 0.1 mm/hr.
    /// </summary>
    public float PrecipRateThresholdMmHr { get; set; } = 0.1f;

    /// <summary>
    /// WX-114 deterministic significance gate: a cost pre-filter that runs after
    /// the WX-80 input-identity pre-filter and before the Claude reconciliation
    /// call.  When the deterministic forecast has not changed materially since the
    /// last <em>sent</em> report (no criterion in <see cref="SignificanceGateConfig"/>
    /// trips), the Claude call is skipped entirely.  It can only <em>suppress</em> a
    /// call, never force a send — Claude still owns the send/skip judgment and the
    /// prose whenever it is invoked.
    /// </summary>
    public SignificanceGateConfig SignificanceGate { get; set; } = new();

    public List<RecipientConfig> Recipients { get; set; } = [];
}

/// <summary>
/// Tunables for the WX-114 deterministic significance gate, bound from the
/// <c>Report:SignificanceGate</c> section.  The per-horizon-tier arrays are indexed
/// T1=0–24h, T2=24–48h, T3=48–72h, T4=72–120h (a four-element array; day 6 / beyond
/// 120h is narrative-only and never gates).  Numeric thresholds are tunable so the
/// gate can start loose and tighten using the DEBUG skip-vs-call data it logs;
/// structural rules (which rows are safety-floor ADDs, the onset-eager/cessation-lazy
/// asymmetry) live in <c>SignificanceGate.cs</c>.
/// </summary>
/// <summary>Operating mode for the WX-114 significance gate.</summary>
public enum SignificanceGateMode
{
    /// <summary>Disabled — never evaluated; every cycle that clears the input-identity pre-filter reaches Claude (pre-WX-114 behaviour).</summary>
    Off,

    /// <summary>Evaluated and its decision logged, but never acted on — always falls through to Claude. A safe observation mode for validating skip-vs-call behaviour against real traffic before enforcing.</summary>
    Shadow,

    /// <summary>Enforced — a "not significant" result skips the Claude call (the cost saving).</summary>
    Enforce,
}

/// <summary>
/// Tunable thresholds for the WX-114 significance gate, bound from the
/// <c>Report:SignificanceGate</c> section of <c>appsettings.shared.json</c> (see
/// <see cref="ReportConfig"/>). As with its parent, <b>every value below is a default
/// overridden by that file when the corresponding key is present and the file parses</b> —
/// the shared config is authoritative; these initializers are the fallback. Only the
/// <em>tunable</em> knobs live here; the gate's fixed bright lines (freeze point, severe /
/// safety wind, the windKt sustained-ceiling tolerance, the horizon-tier bounds) are
/// compile-time constants in <see cref="WxServices.Common.WxThresholds"/>, not settings.
/// </summary>
public class SignificanceGateConfig
{
    /// <summary>Operating mode.  Defaults to <see cref="SignificanceGateMode.Enforce"/> so the cost saving is live and measurable; set <see cref="SignificanceGateMode.Shadow"/> to observe the gate's decisions without suppressing, or <see cref="SignificanceGateMode.Off"/> to disable it.</summary>
    public SignificanceGateMode Mode { get; set; } = SignificanceGateMode.Enforce;

    /// <summary>Per-tier daily high/low change (°F) at or above which a temperature move is significant.  Loosens with horizon: T1..T4.</summary>
    public int[] TempDeltaDegF { get; set; } = [5, 7, 10, 12];

    /// <summary>Per-tier sustained-wind change (kt) at or above which a wind move is significant.  Loosens with horizon: T1..T4.</summary>
    public int[] WindDeltaKt { get; set; } = [12, 15, 18, 20];

    /// <summary>Sustained-wind speed (kt) at or above which wind reaches advisory level; crossing it (ADD) is always significant.</summary>
    public int WindAdvisoryKt { get; set; } = 25;

    /// <summary>Daily-high temperature (°F) marking the heat-advisory line; crossing it is always significant.  Fixed nationally for v1 — regionalizing it is a deferred follow-up.</summary>
    public int HeatAdvisoryDegF { get; set; } = 100;

    // Fixed bright lines the gate keys off — the freeze point and the horizon-tier
    // bounds — are not deployment-tunable, so they live as constants in
    // WxServices.Common.WxThresholds (single source, shared across assemblies), not as
    // settings here. Only the values above are bound from Report:SignificanceGate.
}

/// <summary>
/// Claude API connection settings shared across all services.
/// Bound from the top-level <c>Claude</c> section of appsettings files.
/// The API key is stored in the <c>GlobalSettings</c> database row.
/// </summary>
public class ClaudeConfig
{
    /// <summary>Default per-request HTTP timeout for Claude Messages API calls, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 300;

    public string? ApiKey { get; set; }

    /// <summary>Claude model ID to use for text generation.  Overridden by <c>Claude:Model</c> in <c>appsettings.shared.json</c> (production: <c>claude-sonnet-4-6</c>); this default is the safety net when that config is absent, so it matches production rather than a cheaper model that under-sends hazards.</summary>
    public string Model { get; set; } = "claude-sonnet-4-6";

    /// <summary>
    /// Per-request HTTP timeout for Claude Messages API calls, in seconds.
    /// The WX-79 reconciliation pass routinely generates for 60-100s; the .NET
    /// default <c>HttpClient.Timeout</c> of 100s sat right against that latency
    /// and dropped ~1-in-3 reports (WX-100).  Default gives generous headroom.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}


/// <summary>
/// Per-recipient unit preferences for displayed values.
/// Each preference is independent — recipients can mix units freely
/// (e.g. Celsius temperatures with mph wind speeds).
/// </summary>
public class UnitPreferences
{
    /// <summary>Temperature unit: <c>"F"</c> for Fahrenheit (default) or <c>"C"</c> for Celsius.</summary>
    public string Temperature { get; set; } = "F";

    /// <summary>Pressure unit: <c>"inHg"</c> for inches of mercury (default) or <c>"kPa"</c> for kilopascals.</summary>
    public string Pressure { get; set; } = "inHg";

    /// <summary>Wind speed unit: <c>"mph"</c> for miles per hour (default) or <c>"kph"</c> for kilometres per hour.</summary>
    public string WindSpeed { get; set; } = "mph";
}

/// <summary>
/// Per-recipient configuration.  Recipients are stored in the database <c>Recipients</c>
/// table and managed via WxManager's Recipients tab.
/// </summary>
public class RecipientConfig
{
    /// <summary>
    /// Stable identifier (e.g. "paul-en", "kat").  Should be unique across
    /// all recipients.
    /// </summary>
    public string? Id { get; set; }

    public string Email { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Language for this recipient's reports (e.g. "English", "Spanish", "French").  Null falls back to <see cref="ReportConfig.DefaultLanguage"/>.</summary>
    public string? Language { get; set; }

    /// <summary>IANA timezone name (e.g. "America/Chicago").  Defaults to UTC.</summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>Hour(s) of day (0–23) in the recipient's timezone at which the daily report is sent.  Comma-separated when multiple hours are desired (e.g. "6, 12").  Null falls back to <see cref="ReportConfig.DefaultScheduledSendHours"/>.</summary>
    public string? ScheduledSendHours { get; set; }

    // ── location fields ────────────────────────────────────────────────────────

    /// <summary>
    /// Physical address used solely for one-time geocoding to resolve the
    /// nearest METAR and TAF stations.  Never displayed in reports or subjects.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Human-readable location label used in report subjects and body
    /// (e.g. "The Woodlands").  Inferred from geocoding on first run if absent;
    /// stored so the recipient can override the inferred name.
    /// </summary>
    public string? LocalityName { get; set; }

    /// <summary>
    /// Database key of the <c>Localities</c> row this recipient belongs to, or
    /// <see langword="null"/> when unassigned. For members, stations/timezone/hours
    /// are mirrored from the locality (WX-125/WX-133) and the resolver must not
    /// write competing station values (WX-127).
    /// </summary>
    public long? LocalityId { get; set; }

    /// <summary>Cached latitude from address geocoding.</summary>
    public double? Latitude { get; set; }

    /// <summary>Cached longitude from address geocoding.</summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// Preferred METAR station ICAO(s) for this recipient, in priority order.
    /// The resolver populates this with the single nearest station on first run.
    /// Additional fallback stations can be added manually as a comma-separated
    /// list (e.g. "KDWH, KHOU").  At runtime, stations are tried in order;
    /// if none have recent data the service falls back to any available station
    /// in the database for that cycle without updating this field.
    /// </summary>
    public string? MetarIcao { get; set; }

    /// <summary>Cached ICAO of the nearest TAF station to this recipient.</summary>
    public string? TafIcao { get; set; }

    /// <summary>Preferred units for displayed values. Each dimension is independent; defaults to US customary.</summary>
    public UnitPreferences Units { get; set; } = new();
}