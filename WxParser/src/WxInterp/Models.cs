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
    public SkyCoverage Coverage          { get; init; }
    /// <summary>Cloud base or vertical-visibility height in feet AGL, or null when not reported.</summary>
    public int?        HeightFeet        { get; init; }
    /// <summary>Significant cloud type (CB or TCU), if appended to the sky group.</summary>
    public CloudType   CloudType         { get; init; }
    /// <summary>True when this layer represents a vertical visibility (VV) group rather than a cloud layer.</summary>
    public bool        IsVerticalVisibility { get; init; }
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
    public WeatherIntensity       Intensity     { get; init; }
    /// <summary>Descriptor qualifier (showers, thunderstorm, freezing, etc.), or null if absent.</summary>
    public WeatherDescriptor?     Descriptor    { get; init; }
    /// <summary>One or more precipitation types present in the phenomenon group.</summary>
    public IReadOnlyList<PrecipitationType> Precipitation { get; init; } = [];
    /// <summary>Obscuration type (fog, mist, haze, etc.), or null if absent.</summary>
    public WeatherObscuration?    Obscuration   { get; init; }
    /// <summary>Other phenomenon (squall, funnel cloud, etc.), or null if absent.</summary>
    public OtherPhenomenon?       Other         { get; init; }
    /// <summary>True when this phenomenon appeared in a RE- (recent weather) group.</summary>
    public bool                   IsRecent      { get; init; }
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
    public ForecastChangeType  ChangeType            { get; init; }
    /// <summary>UTC start of this period's validity window, or null for periods without an explicit from-time.</summary>
    public DateTime?           ValidFromUtc          { get; init; }
    /// <summary>UTC end of this period's validity window, or null for FM periods (open-ended until the next FM or TAF end).</summary>
    public DateTime?           ValidToUtc            { get; init; }

    // wind
    /// <summary>Forecast wind direction in degrees true, or null when variable or not reported.</summary>
    public int?  WindDirectionDeg { get; init; }
    /// <summary>True when the forecast wind direction is variable (VRB).</summary>
    public bool  WindIsVariable   { get; init; }
    /// <summary>Forecast mean wind speed in knots, or null when not reported.</summary>
    public int?  WindSpeedKt      { get; init; }
    /// <summary>Forecast gust speed in knots, or null when not reported.</summary>
    public int?  WindGustKt       { get; init; }

    // visibility
    /// <summary>True when CAVOK was forecast (ceiling and visibility OK).</summary>
    public bool    Cavok                  { get; init; }
    /// <summary>Forecast prevailing visibility in statute miles, or null when not reported or CAVOK.</summary>
    public double? VisibilityStatuteMiles { get; init; }

    /// <summary>Forecast sky conditions, ordered from lowest to highest layer.</summary>
    public IReadOnlyList<SkyLayer>      SkyLayers        { get; init; } = [];
    /// <summary>Forecast weather phenomena for this period.</summary>
    public IReadOnlyList<SnapshotWeather> WeatherPhenomena { get; init; } = [];
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
    // ── observation metadata ──────────────────────────────────────────────────

    /// <summary>ICAO identifier of the home METAR station.</summary>
    public string   StationIcao       { get; init; } = "";
    /// <summary>Human-readable locality name from configuration (e.g. "Spring").</summary>
    public string   LocalityName      { get; init; } = "";
    /// <summary>UTC time of the METAR observation.</summary>
    public DateTime ObservationTimeUtc { get; init; }
    /// <summary>True when the observation was made by an automated station (AUTO).</summary>
    public bool     IsAutomated       { get; init; }

    // ── wind ─────────────────────────────────────────────────────────────────

    /// <summary>Wind direction in degrees true, or null when variable.</summary>
    public int?  WindDirectionDeg { get; init; }
    /// <summary>True when the wind direction is variable (VRB).</summary>
    public bool  WindIsVariable   { get; init; }
    /// <summary>Mean wind speed in knots, or null when calm/not reported.</summary>
    public int?  WindSpeedKt      { get; init; }
    /// <summary>Gust speed in knots, or null when not reported.</summary>
    public int?  WindGustKt       { get; init; }

    // ── visibility ────────────────────────────────────────────────────────────

    /// <summary>True when CAVOK was reported.</summary>
    public bool    Cavok                  { get; init; }
    /// <summary>Prevailing visibility in statute miles, or null when not reported.</summary>
    public double? VisibilityStatuteMiles { get; init; }
    /// <summary>True when the visibility was reported as less than the stated value (M qualifier).</summary>
    public bool    VisibilityLessThan     { get; init; }

    // ── sky ───────────────────────────────────────────────────────────────────

    /// <summary>Sky conditions, ordered from lowest to highest layer.</summary>
    public IReadOnlyList<SkyLayer> SkyLayers { get; init; } = [];

    // ── weather ───────────────────────────────────────────────────────────────

    /// <summary>Present and recent weather phenomena decoded from the METAR.</summary>
    public IReadOnlyList<SnapshotWeather> WeatherPhenomena { get; init; } = [];

    // ── temperature ───────────────────────────────────────────────────────────

    /// <summary>Air temperature in degrees Celsius, or null when not reported.</summary>
    public double? TemperatureCelsius    { get; init; }
    /// <summary>Air temperature converted to degrees Fahrenheit, or null when not reported.</summary>
    public double? TemperatureFahrenheit { get; init; }
    /// <summary>Dew-point temperature in degrees Celsius, or null when not reported.</summary>
    public double? DewPointCelsius       { get; init; }

    // ── altimeter ─────────────────────────────────────────────────────────────

    /// <summary>Altimeter setting in inches of mercury.</summary>
    public double? AltimeterInHg { get; init; }

    // ── forecast ─────────────────────────────────────────────────────────────

    /// <summary>ICAO identifier of the TAF station used for the forecast, or null if unavailable.</summary>
    public string? TafStationIcao { get; init; }
    /// <summary>All TAF periods in order (BASE first, then change groups).</summary>
    public IReadOnlyList<ForecastPeriod> ForecastPeriods { get; init; } = [];
}
