namespace MetarParser.Data.Entities;

/// <summary>
/// Persists the configuration and resolved location data for a weather report recipient.
/// Managed via WxManager's Recipients tab.
/// </summary>
/// <remarks>
/// Unit-preference columns (<see cref="TempUnit"/>, <see cref="PressureUnit"/>,
/// <see cref="WindSpeedUnit"/>) are the flattened equivalent of the <c>Units</c> nested
/// object in the legacy config model.
/// <para>
/// <see cref="RecipientId"/> is a logical foreign key to
/// <see cref="RecipientState.RecipientId"/>; the relationship is not enforced by a
/// database constraint to avoid migration friction with pre-existing state rows.
/// </para>
/// </remarks>
public class Recipient
{
    /// <summary>Auto-incremented surrogate key.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Stable application-level identifier (e.g. <c>"paulh"</c>).
    /// Must be unique and must match <see cref="RecipientState.RecipientId"/> for the
    /// corresponding state row.
    /// </summary>
    public string RecipientId { get; set; } = "";

    /// <summary>Recipient email address.</summary>
    public string Email { get; set; } = "";

    /// <summary>Display name used in report salutations and email subjects.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Language for generated reports (e.g. <c>"English"</c>, <c>"Spanish"</c>).
    /// <see langword="null"/> falls back to the service default.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// IANA timezone name (e.g. <c>"America/Chicago"</c>). Defaults to UTC.
    /// For locality members this is mirrored verbatim from
    /// <see cref="Entities.Locality.Timezone"/> (WX-133) — a locality spans
    /// exactly one timezone, so the locality is authoritative.
    /// </summary>
    public string Timezone { get; set; } = "UTC";

    /// <summary>
    /// Comma-separated hour(s) of day (0–23) in the recipient's timezone for
    /// daily scheduled sends (e.g. <c>"7"</c> or <c>"6, 12"</c>).
    /// <see langword="null"/> falls back to the service default.
    /// The runtime reads this column for every recipient; for locality members it
    /// carries the locality's value, mirrored verbatim from
    /// <see cref="Entities.Locality.ScheduledSendHours"/> (WX-133) — the locality
    /// is authoritative, and only for recipients without a locality is this an
    /// independent per-recipient setting.
    /// </summary>
    public string? ScheduledSendHours { get; set; }

    /// <summary>Physical address used once for geocoding. Never displayed in reports.</summary>
    public string? Address { get; set; }

    /// <summary>
    /// Human-readable locality label used in report subjects and body
    /// (e.g. <c>"The Woodlands"</c>). Populated by the resolver on first run if absent.
    /// </summary>
    public string? LocalityName { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="Entities.Locality"/> this recipient belongs to,
    /// or <see langword="null"/> when unassigned. Assigning a locality mirrors that
    /// locality's display name, station hierarchy, timezone, and scheduled send
    /// hours verbatim onto this recipient (<see cref="LocalityName"/>,
    /// <see cref="MetarIcao"/>, <see cref="TafIcao"/>, <see cref="Timezone"/>,
    /// <see cref="ScheduledSendHours"/>) — the locality is authoritative
    /// (see <c>LocalityAssignment</c>).
    /// </summary>
    public long? LocalityId { get; set; }

    /// <summary>Navigation to the recipient's locality; <see langword="null"/> when unassigned.</summary>
    public Locality? Locality { get; set; }

    /// <summary>Cached latitude from address geocoding.</summary>
    public double? Latitude { get; set; }

    /// <summary>Cached longitude from address geocoding.</summary>
    public double? Longitude { get; set; }

    /// <summary>
    /// Preferred METAR station ICAO(s) in priority order, comma-separated
    /// (e.g. <c>"KDWH, KHOU"</c>). Populated by the resolver on first run.
    /// </summary>
    public string? MetarIcao { get; set; }

    /// <summary>
    /// Nearest TAF station ICAO. The sentinel value <c>"NONE"</c> means a lookup
    /// was attempted but no station was found. Populated by the resolver on first run.
    /// </summary>
    public string? TafIcao { get; set; }

    /// <summary>Temperature unit: <c>"F"</c> (Fahrenheit, default) or <c>"C"</c> (Celsius).</summary>
    public string TempUnit { get; set; } = "F";

    /// <summary>Pressure unit: <c>"inHg"</c> (default) or <c>"kPa"</c>.</summary>
    public string PressureUnit { get; set; } = "inHg";

    /// <summary>Wind speed unit: <c>"mph"</c> (default) or <c>"kph"</c>.</summary>
    public string WindSpeedUnit { get; set; } = "mph";

    /// <summary>
    /// Precipitation unit: <c>"in"</c> (inches, default) or <c>"mm"</c> (millimetres).
    /// The structured report's canonical precipitation quantity is millimetres
    /// (<c>{q:precip_mm:…}</c>); the WX-129 deterministic renderer converts to this
    /// unit per recipient. Independent of the other unit fields (WX-142 / Fork-2).
    /// </summary>
    public string PrecipUnit { get; set; } = "in";

    /// <summary>
    /// .NET culture name controlling number formatting (decimal separator, digit
    /// grouping, time format) when the renderer substitutes quantity tokens —
    /// e.g. <c>"en-US"</c>, <c>"es-US"</c>. <see langword="null"/> until set.
    /// Added by WX-142 but deliberately <em>unread</em> for now: WX-129 formats all
    /// recipients with US / period-decimal conventions, and WX-138 wires this
    /// preference into the renderer (decoupling number format from language).
    /// </summary>
    public string? NumberFormat { get; set; }
}