// Configuration model for the WxReport.Svc service.
// Populated from the "Report" section of appsettings files.
// Secrets (ApiKey, Smtp.Password) must be provided in appsettings.local.json,
// which is excluded from source control.
//
// SmtpConfig is defined in WxServices.Common and bound from the top-level
// "Smtp" section, shared with other services.

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// Root configuration model for WxReport.Svc.
/// Bound from the <c>Report</c> section of appsettings files at runtime.
/// Secrets (Claude API key, SMTP password) must be placed in <c>appsettings.local.json</c>.
/// </summary>
public class ReportConfig
{
    /// <summary>Language to use when no per-recipient language is specified (e.g. "English", "Spanish").</summary>
    public string DefaultLanguage         { get; set; } = "English";

    /// <summary>Hour of day (0–23, in the recipient's local timezone) at which the daily scheduled report is sent.</summary>
    public int    DefaultScheduledSendHour { get; set; } = 7;

    /// <summary>Minimum minutes that must elapse between any two reports sent to the same recipient.</summary>
    public int    MinGapMinutes           { get; set; } = 60;

    /// <summary>Check interval; loaded from appsettings.json (service-specific).</summary>
    public int    IntervalMinutes         { get; set; } = 5;

    /// <summary>Path to the heartbeat file written after each successful report cycle. Read by WxMonitor.Svc.</summary>
    public string?                 HeartbeatFile     { get; set; }

    /// <summary>
    /// Minimum precipitation rate in mm/hr for a GFS forecast hour to be counted
    /// as precipitation in the daily summary.  Hours below this threshold are
    /// treated as dry.  Defaults to 0.1 mm/hr.
    /// </summary>
    public float PrecipRateThresholdMmHr { get; set; } = 0.1f;

    public SignificantChangeConfig SignificantChange { get; set; } = new();
    public List<RecipientConfig>   Recipients        { get; set; } = [];
}

/// <summary>Thresholds used by <see cref="SnapshotFingerprint"/> to determine whether a weather change is significant enough to trigger an unscheduled report.</summary>
public class SignificantChangeConfig
{
    /// <summary>Wind speed at or above this threshold (kt) is considered a significant condition.</summary>
    public int    WindThresholdKt       { get; set; } = 25;

    /// <summary>Visibility below this threshold (SM) is considered a significant condition.</summary>
    public double VisibilityThresholdSm { get; set; } = 3.0;

    /// <summary>Ceiling below this threshold (ft AGL) is considered a significant condition.</summary>
    public int    CeilingThresholdFt    { get; set; } = 3000;

    /// <summary>
    /// Minimum change in the GFS forecast high temperature (°F) between report
    /// cycles that triggers an unscheduled alert.  The fingerprint buckets the
    /// forecast high to this resolution, so a change of this many degrees or more
    /// will produce a different fingerprint.  Default 15°F.
    /// </summary>
    public int    ForecastHighChangeDegF { get; set; } = 15;

    /// <summary>
    /// Surface-based CAPE threshold in J/kg above which a day is considered to
    /// carry meaningful thunderstorm potential in the fingerprint.  When any
    /// forecast day crosses this threshold (or stops crossing it), the fingerprint
    /// changes and an unscheduled alert is sent.  Default 1000 J/kg.
    /// </summary>
    public int    CapeThresholdJKg      { get; set; } = 1000;

    /// <summary>
    /// Maximum precipitation rate in mm/hr that any GFS forecast day must reach or
    /// exceed to be counted as having meaningful precipitation in the fingerprint
    /// (<c>GP</c> field).  When any day crosses this threshold (or drops below it),
    /// the fingerprint changes and an unscheduled update may be sent.
    /// Default 2.0 mm/hr (moderate rain).
    /// </summary>
    public float  GfsPrecipThresholdMmHr { get; set; } = 2.0f;
}

/// <summary>
/// Claude API connection settings shared across all services.
/// Bound from the top-level <c>Claude</c> section of appsettings files.
/// The API key must come from <c>appsettings.local.json</c>.
/// The model can be overridden per-service in that service's own appsettings files.
/// </summary>
public class ClaudeConfig
{
    public string? ApiKey { get; set; }

    /// <summary>Claude model ID to use for text generation.</summary>
    public string  Model  { get; set; } = "claude-haiku-4-5-20251001";
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
    public string Pressure    { get; set; } = "inHg";

    /// <summary>Wind speed unit: <c>"mph"</c> for miles per hour (default) or <c>"kph"</c> for kilometres per hour.</summary>
    public string WindSpeed   { get; set; } = "mph";
}

/// <summary>
/// Per-recipient configuration.  Static fields (email, name, language, timezone, schedule)
/// live in <c>appsettings.json</c>; resolved location fields (coordinates, station ICAOs)
/// are written by <see cref="RecipientResolver"/> to <c>appsettings.local.json</c>.
/// </summary>
public class RecipientConfig
{
    /// <summary>
    /// Stable identifier used to match this entry when writing resolved data back
    /// to appsettings.local.json (e.g. "paul-en", "kat").  Should be unique across
    /// all recipients.  Falls back to Email matching if absent.
    /// </summary>
    public string? Id                { get; set; }

    public string  Email             { get; set; } = "";
    public string  Name              { get; set; } = "";

    /// <summary>Language for this recipient's reports (e.g. "English", "Spanish", "French").  Null falls back to <see cref="ReportConfig.DefaultLanguage"/>.</summary>
    public string? Language          { get; set; }

    /// <summary>IANA timezone name (e.g. "America/Chicago").  Defaults to UTC.</summary>
    public string  Timezone          { get; set; } = "UTC";

    /// <summary>Hour of day (0–23) in the recipient's timezone at which the daily report is sent.  Null falls back to <see cref="ReportConfig.DefaultScheduledSendHour"/>.</summary>
    public int?    ScheduledSendHour { get; set; }

    // ── location fields (all stored in appsettings.local.json) ───────────────

    /// <summary>
    /// Physical address used solely for one-time geocoding to resolve the
    /// nearest METAR and TAF stations.  Never displayed in reports or subjects.
    /// </summary>
    public string? Address      { get; set; }

    /// <summary>
    /// Human-readable location label used in report subjects and body
    /// (e.g. "The Woodlands").  Inferred from geocoding on first run if absent;
    /// stored so the recipient can override the inferred name.
    /// </summary>
    public string? LocalityName { get; set; }

    /// <summary>Cached latitude from address geocoding.</summary>
    public double? Latitude     { get; set; }

    /// <summary>Cached longitude from address geocoding.</summary>
    public double? Longitude    { get; set; }

    /// <summary>
    /// Preferred METAR station ICAO(s) for this recipient, in priority order.
    /// The resolver populates this with the single nearest station on first run.
    /// Additional fallback stations can be added manually as a comma-separated
    /// list (e.g. "KDWH, KHOU").  At runtime, stations are tried in order;
    /// if none have recent data the service falls back to any available station
    /// in the database for that cycle without updating this field.
    /// </summary>
    public string? MetarIcao    { get; set; }

    /// <summary>Cached ICAO of the nearest TAF station to this recipient.</summary>
    public string? TafIcao      { get; set; }

    /// <summary>Preferred units for displayed values. Each dimension is independent; defaults to US customary.</summary>
    public UnitPreferences Units { get; set; } = new();
}
