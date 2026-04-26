namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>Metars</c> table.
/// Each row corresponds to a single decoded METAR or SPECI report.
/// Scalar decoded fields are stored as individual columns.
/// Multi-valued groups (sky conditions, weather phenomena, RVR) are stored
/// both as raw strings in this table and as structured rows in child tables.
/// </summary>
public sealed class MetarRecord
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Report type: <c>"METAR"</c> or <c>"SPECI"</c>.
    /// </summary>
    public string ReportType { get; set; } = "";

    /// <summary>Four-letter ICAO station identifier, e.g. <c>"EGLL"</c>.</summary>
    public string StationIcao { get; set; } = "";

    /// <summary>
    /// Inferred UTC date and time of the observation.
    /// Because METAR reports omit the year and month, the full date is derived
    /// by assuming the reported day number belongs to the most recent calendar
    /// month in which that day occurred relative to the time of receipt.
    /// </summary>
    public DateTime ObservationUtc { get; set; }

    /// <summary>
    /// <see langword="true"/> when the <c>AUTO</c> modifier was present in the report,
    /// indicating a fully automated observation.
    /// </summary>
    public bool IsAuto { get; set; }

    /// <summary>
    /// <see langword="true"/> when the <c>COR</c> modifier was present,
    /// indicating this is a corrected report.
    /// </summary>
    public bool IsCorrection { get; set; }

    // ── wind ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wind direction in degrees true (000–360).
    /// <see langword="null"/> when direction is variable (VRB) or wind is not reported.
    /// </summary>
    public int? WindDirection { get; set; }

    /// <summary>
    /// <see langword="true"/> when the wind direction was reported as VRB (variable).
    /// </summary>
    public bool WindIsVariable { get; set; }

    /// <summary>
    /// Mean wind speed in the unit given by <see cref="WindUnit"/>.
    /// <see langword="null"/> when wind is not reported.
    /// </summary>
    public int? WindSpeed { get; set; }

    /// <summary>
    /// Gust speed in the unit given by <see cref="WindUnit"/>.
    /// <see langword="null"/> when no gust is reported.
    /// </summary>
    public int? WindGust { get; set; }

    /// <summary>
    /// Wind speed unit: <c>"KT"</c> (knots) or <c>"MPS"</c> (meters per second).
    /// <see langword="null"/> when wind is not reported.
    /// </summary>
    public string? WindUnit { get; set; }

    /// <summary>
    /// Start bearing of the variable wind sector in degrees true.
    /// <see langword="null"/> when no variable-sector group was present.
    /// </summary>
    public int? WindVariableFrom { get; set; }

    /// <summary>
    /// End bearing of the variable wind sector in degrees true.
    /// <see langword="null"/> when no variable-sector group was present.
    /// </summary>
    public int? WindVariableTo { get; set; }

    // ── visibility ──────────────────────────────────────────────────────────

    /// <summary>
    /// <see langword="true"/> when <c>CAVOK</c> was reported.
    /// When <see langword="true"/>, visibility and sky columns are not populated.
    /// </summary>
    public bool VisibilityCavok { get; set; }

    /// <summary>
    /// Prevailing visibility in meters (WMO format).
    /// <see langword="null"/> when CAVOK is reported or the US statute-mile format is used.
    /// </summary>
    public int? VisibilityM { get; set; }

    /// <summary>
    /// Prevailing visibility in statute miles (US format).
    /// <see langword="null"/> when the WMO meter format is used.
    /// </summary>
    public double? VisibilityStatuteMiles { get; set; }

    /// <summary>
    /// <see langword="true"/> when the visibility was reported with an M (less-than) prefix.
    /// </summary>
    public bool VisibilityLessThan { get; set; }

    // ── temperature ─────────────────────────────────────────────────────────

    /// <summary>
    /// Air temperature in degrees Celsius.
    /// <see langword="null"/> when the temperature group is absent.
    /// </summary>
    public double? AirTemperatureCelsius { get; set; }

    /// <summary>
    /// Dew-point temperature in degrees Celsius.
    /// <see langword="null"/> when the temperature group is absent or the dew point is missing.
    /// </summary>
    public double? DewPointCelsius { get; set; }

    // ── altimeter ───────────────────────────────────────────────────────────

    /// <summary>
    /// Altimeter setting value: whole hectopascals when <see cref="AltimeterUnit"/> is
    /// <c>"hPa"</c>, or inches of mercury to two decimal places when <c>"inHg"</c>.
    /// <see langword="null"/> when the altimeter group is absent.
    /// </summary>
    public double? AltimeterValue { get; set; }

    /// <summary>
    /// Altimeter setting unit: <c>"hPa"</c> or <c>"inHg"</c>.
    /// <see langword="null"/> when the altimeter group is absent.
    /// </summary>
    public string? AltimeterUnit { get; set; }

    // ── raw multi-value strings ──────────────────────────────────────────────

    /// <summary>
    /// Sky condition tokens from the report joined into a single string,
    /// e.g. <c>"SCT030 BKN100 OVC250"</c>.
    /// <see langword="null"/> when no sky condition groups were present.
    /// See also <see cref="SkyConditions"/> for the structured child rows.
    /// </summary>
    public string? RawSkyConditions { get; set; }

    /// <summary>
    /// Present and recent weather tokens joined into a single string,
    /// e.g. <c>"-RA BR"</c>.
    /// <see langword="null"/> when no weather phenomena were reported.
    /// See also <see cref="WeatherPhenomena"/> for the structured child rows.
    /// </summary>
    public string? RawWeatherPhenomena { get; set; }

    /// <summary>
    /// Runway visual range tokens joined into a single string,
    /// e.g. <c>"R28L/0600N R28R/0700U"</c>.
    /// <see langword="null"/> when no RVR groups were present.
    /// See also <see cref="RunwayVisualRanges"/> for the structured child rows.
    /// </summary>
    public string? RawRunwayVisualRange { get; set; }

    // ── remarks / raw ────────────────────────────────────────────────────────

    /// <summary>
    /// Free-text content following the <c>RMK</c> token.
    /// <see langword="null"/> when no remarks section was present.
    /// </summary>
    public string? Remarks { get; set; }

    /// <summary>
    /// The complete original report string exactly as received, before any parsing.
    /// This is the authoritative source of record and can be used to re-parse
    /// if the parser is updated.
    /// </summary>
    public string RawReport { get; set; } = "";

    /// <summary>
    /// UTC date and time at which this row was inserted into the database.
    /// </summary>
    public DateTime ReceivedUtc { get; set; }

    // ── navigation properties ────────────────────────────────────────────────

    /// <summary>Structured sky condition rows for this observation.</summary>
    public List<MetarSkyCondition> SkyConditions { get; set; } = [];

    /// <summary>Structured weather phenomenon rows for this observation.</summary>
    public List<MetarWeatherPhenomenon> WeatherPhenomena { get; set; } = [];

    /// <summary>Structured runway visual range rows for this observation.</summary>
    public List<MetarRunwayVisualRange> RunwayVisualRanges { get; set; } = [];
}