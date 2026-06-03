// Configuration model for the WxReport.Svc service.
// Non-secret settings are populated from the "Report" section of appsettings files.
// Secrets (SMTP credentials, Claude API key) are stored in the GlobalSettings
// database row and loaded at runtime — they never appear in config files.

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// Root configuration model for WxReport.Svc.
/// Bound from the <c>Report</c> section of appsettings files at runtime.
/// </summary>
public class ReportConfig
{
    /// <summary>Language to use when no per-recipient language is specified (e.g. "English", "Spanish").</summary>
    public string DefaultLanguage { get; set; } = "English";

    /// <summary>Hour(s) of day (0–23, in the recipient's local timezone) at which the daily scheduled report is sent.  Comma-separated when multiple hours are desired (e.g. "6, 12").</summary>
    public string DefaultScheduledSendHours { get; set; } = "7";

    /// <summary>
    /// Minimum minutes that must elapse between any two reports sent to the same
    /// recipient.  Raised from 60 to 90 in WX-110: send-gap analysis showed 61% of
    /// per-recipient gaps clustered at the 60-minute floor — the rate limit, not the
    /// weather, was setting the cadence — so the gate was effectively sending hourly.
    /// 90 caps that without materially delaying genuine updates (it does, however,
    /// defer a rapid-onset hazard update by up to 90 min vs 60; revisit if that bites).
    /// </summary>
    public int MinGapMinutes { get; set; } = 90;

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

    public List<RecipientConfig> Recipients { get; set; } = [];
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

    /// <summary>Claude model ID to use for text generation.</summary>
    public string Model { get; set; } = "claude-haiku-4-5-20251001";

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