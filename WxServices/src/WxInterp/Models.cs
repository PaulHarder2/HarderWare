// WxInterp models — language-neutral structured representation of current
// weather conditions and TAF forecast periods.
//
// All values use standard units (knots, statute miles, °C and °F, feet).
// String fields are limited to identifiers; all human-readable content is
// expressed as enum values so that language generation can be deferred to
// the consumer (e.g. WxReport).

namespace WxInterp;

// ── sky ──────────────────────────────────────────────────────────────────────

/// <summary>Sky coverage amount, derived from the METAR/TAF cover code.</summary>
public enum SkyCoverage
{
    /// <summary>SKC or CLR — sky clear.</summary>
    Clear,
    /// <summary>FEW — 1–2 oktas.</summary>
    Few,
    /// <summary>SCT — 3–4 oktas.</summary>
    Scattered,
    /// <summary>BKN — 5–7 oktas.</summary>
    Broken,
    /// <summary>OVC — 8 oktas, sky overcast.</summary>
    Overcast,
    /// <summary>VV — sky obscured, vertical visibility reported.</summary>
    VerticalVisibility,
    /// <summary>NSC — no significant cloud below 5000 ft, no CB, CAVOK not applicable.</summary>
    NoSignificantCloud,
    /// <summary>NCD — automated station detected no cloud.</summary>
    NoCloudsDetected,
}

/// <summary>Significant cloud type appended to a sky-condition group.</summary>
public enum CloudType { None, Cumulonimbus, ToweringCumulus }

/// <summary>A single sky-condition or cloud layer.</summary>
public sealed class SkyLayer
{
    /// <summary>Coverage amount decoded from the cover code (FEW, SCT, BKN, OVC, etc.).</summary>
    public SkyCoverage Coverage { get; init; }
    /// <summary>Cloud base or vertical-visibility height in feet AGL, or null when not reported.</summary>
    public int? HeightFeet { get; init; }
    /// <summary>Significant cloud type (CB or TCU), if appended to the sky group.</summary>
    public CloudType CloudType { get; init; }
    /// <summary>True when this layer represents a vertical visibility (VV) group rather than a cloud layer.</summary>
    public bool IsVerticalVisibility { get; init; }
}

// ── weather phenomena ─────────────────────────────────────────────────────────

/// <summary>Intensity qualifier for a weather phenomenon.</summary>
public enum WeatherIntensity { Light, Moderate, Heavy, InTheVicinity }

/// <summary>Descriptor qualifier (SH, TS, FZ, etc.).</summary>
public enum WeatherDescriptor
{
    Shallow, Partial, Patches, LowDrifting, Blowing,
    Showers, Thunderstorm, Freezing,
}

/// <summary>Precipitation type.</summary>
public enum PrecipitationType
{
    Drizzle, Rain, Snow, SnowGrains, IceCrystals,
    IcePellets, Hail, SmallHail, Unknown,
}

/// <summary>Obscuration type.</summary>
public enum WeatherObscuration { Mist, Fog, Smoke, VolcanicAsh, Dust, Sand, Haze, Spray }

/// <summary>Other phenomenon (tornado, squall, etc.).</summary>
public enum OtherPhenomenon { DustSandWhirls, Squalls, FunnelCloud, Sandstorm, Duststorm }

/// <summary>A single decoded weather phenomenon group.</summary>
public sealed class SnapshotWeather
{
    /// <summary>Intensity qualifier (light, moderate, heavy, or in the vicinity).</summary>
    public WeatherIntensity Intensity { get; init; }
    /// <summary>Descriptor qualifier (showers, thunderstorm, freezing, etc.), or null if absent.</summary>
    public WeatherDescriptor? Descriptor { get; init; }
    /// <summary>One or more precipitation types present in the phenomenon group.</summary>
    public IReadOnlyList<PrecipitationType> Precipitation { get; init; } = [];
    /// <summary>Obscuration type (fog, mist, haze, etc.), or null if absent.</summary>
    public WeatherObscuration? Obscuration { get; init; }
    /// <summary>Other phenomenon (squall, funnel cloud, etc.), or null if absent.</summary>
    public OtherPhenomenon? Other { get; init; }
    /// <summary>True when this phenomenon appeared in a RE- (recent weather) group.</summary>
    public bool IsRecent { get; init; }
}

// ── forecast ─────────────────────────────────────────────────────────────────

/// <summary>TAF change-period type.</summary>
public enum ForecastChangeType
{
    /// <summary>The initial base forecast period.</summary>
    Base,
    /// <summary>BECMG — conditions expected to change gradually.</summary>
    BecomeGradually,
    /// <summary>TEMPO — temporary fluctuations.</summary>
    Temporary,
    /// <summary>FM — conditions from a specific time.</summary>
    From,
    /// <summary>PROB30 — 30% probability of the conditions occurring.</summary>
    Probability30,
    /// <summary>PROB40 — 40% probability of the conditions occurring.</summary>
    Probability40,
    /// <summary>PROB30 TEMPO — 30% probability of temporary conditions.</summary>
    Probability30Temporary,
    /// <summary>PROB40 TEMPO — 40% probability of temporary conditions.</summary>
    Probability40Temporary,
}

/// <summary>A single TAF forecast period (base or change group).</summary>
public sealed class ForecastPeriod
{
    /// <summary>How this period was introduced (BASE, BECMG, TEMPO, FM, PROB30, PROB40, etc.).</summary>
    public ForecastChangeType ChangeType { get; init; }
    /// <summary>UTC start of this period's validity window, or null for periods without an explicit from-time.</summary>
    public DateTime? ValidFromUtc { get; init; }
    /// <summary>UTC end of this period's validity window, or null for FM periods (open-ended until the next FM or TAF end).</summary>
    public DateTime? ValidToUtc { get; init; }

    // wind
    /// <summary>Forecast wind direction in degrees true, or null when variable or not reported.</summary>
    public int? WindDirectionDeg { get; init; }
    /// <summary>True when the forecast wind direction is variable (VRB).</summary>
    public bool WindIsVariable { get; init; }
    /// <summary>Forecast mean wind speed in knots, or null when not reported.</summary>
    public int? WindSpeedKt { get; init; }
    /// <summary>Forecast gust speed in knots, or null when not reported.</summary>
    public int? WindGustKt { get; init; }

    // visibility
    /// <summary>True when CAVOK was forecast (ceiling and visibility OK).</summary>
    public bool Cavok { get; init; }
    /// <summary>Forecast prevailing visibility in statute miles, or null when not reported or CAVOK.</summary>
    public double? VisibilityStatuteMiles { get; init; }

    /// <summary>Forecast sky conditions, ordered from lowest to highest layer.</summary>
    public IReadOnlyList<SkyLayer> SkyLayers { get; init; } = [];
    /// <summary>Forecast weather phenomena for this period.</summary>
    public IReadOnlyList<SnapshotWeather> WeatherPhenomena { get; init; } = [];
}

// ── GFS model forecast ────────────────────────────────────────────────────────

/// <summary>
/// GFS model-forecast summary for a single calendar day (UTC) at the recipient's
/// exact location, produced by bilinear interpolation over the four surrounding
/// GFS 0.25° grid points.
/// <para>
/// Temperature high/low span all forecast hours that fall within the UTC calendar
/// day.  Wind, cloud cover, CAPE, and precipitation represent the maximum value
/// observed during any forecast hour in the day.
/// </para>
/// </summary>
public sealed class GfsDailyForecast
{
    /// <summary>UTC calendar date this summary covers.</summary>
    public DateOnly Date { get; init; }
    /// <summary>Forecast high 2-metre temperature in degrees Celsius, or null if not available.</summary>
    public float? HighTempC { get; init; }
    /// <summary>Forecast high 2-metre temperature in degrees Fahrenheit, or null if not available.</summary>
    public float? HighTempF { get; init; }
    /// <summary>Forecast low 2-metre temperature in degrees Celsius, or null if not available.</summary>
    public float? LowTempC { get; init; }
    /// <summary>Forecast low 2-metre temperature in degrees Fahrenheit, or null if not available.</summary>
    public float? LowTempF { get; init; }
    /// <summary>Maximum sustained 10-metre wind speed during the day in knots, or null if not available.</summary>
    public float? MaxWindSpeedKt { get; init; }
    /// <summary>Wind direction in degrees true at the hour of maximum wind speed, or null if not available.</summary>
    public int? DominantWindDirDeg { get; init; }
    /// <summary>Maximum total cloud cover during the day as a percentage (0–100), or null if not available.</summary>
    public float? MaxCloudCoverPct { get; init; }
    /// <summary>Maximum surface-based CAPE during the day in J/kg, or null if not available.</summary>
    public float? MaxCapeJKg { get; init; }
    /// <summary>
    /// Maximum precipitation rate during the day in mm/hr, or null when all forecast
    /// hours are below the configured threshold or data is unavailable.
    /// </summary>
    public float? MaxPrecipRateMmHr { get; init; }
}

/// <summary>
/// GFS model forecast covering multiple calendar days at the recipient's exact
/// location, produced by bilinear interpolation over the four surrounding 0.25°
/// grid points.
/// </summary>
public sealed class GfsForecast
{
    /// <summary>UTC initialisation time of the GFS model run used to produce this forecast.</summary>
    public DateTime ModelRunUtc { get; init; }
    /// <summary>Per-day summaries in ascending date order.</summary>
    public IReadOnlyList<GfsDailyForecast> Days { get; init; } = [];
}

// ── snapshot ─────────────────────────────────────────────────────────────────

/// <summary>
/// Language-neutral structured representation of current conditions at the
/// home METAR station and the forecast from the nearest TAF station.
/// All values are in standard units; human-language generation is the
/// responsibility of the consumer.
/// </summary>
public sealed class WeatherSnapshot
{
    // ── observation availability ──────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when this snapshot carries current-conditions data
    /// from a METAR observation.  <see langword="false"/> when no qualifying
    /// station was found (see <see cref="ObservationUnavailableNote"/>); in that
    /// case the METAR-derived fields below are left at defaults and only the
    /// forecast sections (<see cref="ForecastPeriods"/>, <see cref="GfsForecast"/>)
    /// carry usable data.
    /// </summary>
    public bool ObservationAvailable { get; init; } = true;

    /// <summary>
    /// Human-readable explanation of why no observation is available, for
    /// inclusion in the report's Current Conditions section.  Non-null only when
    /// <see cref="ObservationAvailable"/> is <see langword="false"/>.
    /// </summary>
    public string? ObservationUnavailableNote { get; init; }

    /// <summary>
    /// Great-circle distance in kilometres from the recipient's coordinates to
    /// the observing station, when a geographic fallback was used to choose the
    /// station.  <see langword="null"/> when the station is one of the recipient's
    /// preferred stations (no fallback needed) or when the recipient has no
    /// coordinates configured.
    /// </summary>
    public double? ObservationDistanceKm { get; init; }

    // ── observation metadata ──────────────────────────────────────────────────

    /// <summary>ICAO identifier of the home METAR station.</summary>
    public string StationIcao { get; init; } = "";
    /// <summary>
    /// Municipality (city/town) of the METAR station from OurAirports
    /// (e.g. "College Station"), or <see langword="null"/> if unavailable.
    /// </summary>
    public string? StationMunicipality { get; init; }

    /// <summary>
    /// Human-readable airport name of the METAR station, properly cased
    /// (e.g. "Easterwood Airport"), or <see langword="null"/> if unavailable.
    /// </summary>
    public string? StationName { get; init; }

    /// <summary>Human-readable locality name from configuration (e.g. "Spring").</summary>
    public string LocalityName { get; init; } = "";
    /// <summary>UTC time of the METAR observation.</summary>
    public DateTime ObservationTimeUtc { get; init; }
    /// <summary>True when the observation was made by an automated station (AUTO).</summary>
    public bool IsAutomated { get; init; }

    // ── wind ─────────────────────────────────────────────────────────────────

    /// <summary>Wind direction in degrees true, or null when variable.</summary>
    public int? WindDirectionDeg { get; init; }
    /// <summary>True when the wind direction is variable (VRB).</summary>
    public bool WindIsVariable { get; init; }
    /// <summary>Mean wind speed in knots, or null when calm/not reported.</summary>
    public int? WindSpeedKt { get; init; }
    /// <summary>Gust speed in knots, or null when not reported.</summary>
    public int? WindGustKt { get; init; }

    // ── visibility ────────────────────────────────────────────────────────────

    /// <summary>True when CAVOK was reported.</summary>
    public bool Cavok { get; init; }
    /// <summary>Prevailing visibility in statute miles, or null when not reported.</summary>
    public double? VisibilityStatuteMiles { get; init; }
    /// <summary>True when the visibility was reported as less than the stated value (M qualifier).</summary>
    public bool VisibilityLessThan { get; init; }

    // ── sky ───────────────────────────────────────────────────────────────────

    /// <summary>Sky conditions, ordered from lowest to highest layer.</summary>
    public IReadOnlyList<SkyLayer> SkyLayers { get; init; } = [];

    // ── weather ───────────────────────────────────────────────────────────────

    /// <summary>Present and recent weather phenomena decoded from the METAR.</summary>
    public IReadOnlyList<SnapshotWeather> WeatherPhenomena { get; init; } = [];

    // ── temperature ───────────────────────────────────────────────────────────

    /// <summary>Air temperature in degrees Celsius, or null when not reported.</summary>
    public double? TemperatureCelsius { get; init; }
    /// <summary>Air temperature converted to degrees Fahrenheit, or null when not reported.</summary>
    public double? TemperatureFahrenheit { get; init; }
    /// <summary>Dew-point temperature in degrees Celsius, or null when not reported.</summary>
    public double? DewPointCelsius { get; init; }

    // ── altimeter ─────────────────────────────────────────────────────────────

    /// <summary>Altimeter setting in inches of mercury.</summary>
    public double? AltimeterInHg { get; init; }

    // ── forecast ─────────────────────────────────────────────────────────────

    /// <summary>ICAO identifier of the TAF station used for the forecast, or null if unavailable.</summary>
    public string? TafStationIcao { get; init; }
    /// <summary>All TAF periods in order (BASE first, then change groups).</summary>
    public IReadOnlyList<ForecastPeriod> ForecastPeriods { get; init; } = [];

    // ── GFS model forecast ────────────────────────────────────────────────────

    /// <summary>
    /// GFS model forecast at the recipient's location, or null if GFS data is
    /// unavailable or coordinates were not provided.
    /// </summary>
    public GfsForecast? GfsForecast { get; init; }
}