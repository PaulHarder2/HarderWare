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
    public SkyCoverage Coverage          { get; init; }
    /// <summary>Cloud base or vertical-visibility height in feet AGL, or null when not reported.</summary>
    public int?        HeightFeet        { get; init; }
    public CloudType   CloudType         { get; init; }
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
    public WeatherIntensity       Intensity     { get; init; }
    public WeatherDescriptor?     Descriptor    { get; init; }
    public IReadOnlyList<PrecipitationType> Precipitation { get; init; } = [];
    public WeatherObscuration?    Obscuration   { get; init; }
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
    Probability30,
    Probability40,
    Probability30Temporary,
    Probability40Temporary,
}

/// <summary>A single TAF forecast period (base or change group).</summary>
public sealed class ForecastPeriod
{
    public ForecastChangeType  ChangeType            { get; init; }
    public DateTime?           ValidFromUtc          { get; init; }
    public DateTime?           ValidToUtc            { get; init; }

    // wind
    public int?  WindDirectionDeg { get; init; }
    public bool  WindIsVariable   { get; init; }
    public int?  WindSpeedKt      { get; init; }
    public int?  WindGustKt       { get; init; }

    // visibility
    public bool    Cavok                  { get; init; }
    public double? VisibilityStatuteMiles { get; init; }

    public IReadOnlyList<SkyLayer>      SkyLayers        { get; init; } = [];
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
    public DateTime ObservationTimeUtc { get; init; }
    /// <summary>True when the observation was made by an automated station (AUTO).</summary>
    public bool     IsAutomated       { get; init; }

    // ── wind ─────────────────────────────────────────────────────────────────

    /// <summary>Wind direction in degrees true, or null when variable.</summary>
    public int?  WindDirectionDeg { get; init; }
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
    public bool    VisibilityLessThan     { get; init; }

    // ── sky ───────────────────────────────────────────────────────────────────

    public IReadOnlyList<SkyLayer> SkyLayers { get; init; } = [];

    // ── weather ───────────────────────────────────────────────────────────────

    public IReadOnlyList<SnapshotWeather> WeatherPhenomena { get; init; } = [];

    // ── temperature ───────────────────────────────────────────────────────────

    public double? TemperatureCelsius    { get; init; }
    public double? TemperatureFahrenheit { get; init; }
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
