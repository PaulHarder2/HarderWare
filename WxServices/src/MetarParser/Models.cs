// Models for parsed METAR/SPECI reports.
// Based on WMO Manual 306, Volume I.1, FM 15 METAR / FM 16 SPECI.

namespace MetarParser;

/// <summary>
/// Decoded wind group, corresponding to WMO 306 FM 15/16 §15.3.
/// Encodes direction, speed, optional gust, unit, and optional variable-sector bounds.
/// Format in the raw report: dddff[Gfmfm]KT|MPS  or  VRBff[Gfmfm]KT|MPS
/// </summary>
public sealed record Wind
{
    /// <summary>
    /// Wind direction in degrees true (000–360), or <see langword="null"/> when the
    /// direction is reported as variable (VRB).
    /// </summary>
    public int? Direction { get; init; }

    /// <summary>
    /// <see langword="true"/> when the direction token is VRB, indicating the wind
    /// direction varies too much to report a single value.
    /// When <see langword="true"/>, <see cref="Direction"/> is <see langword="null"/>.
    /// </summary>
    public bool IsVariable { get; init; }

    /// <summary>
    /// Mean wind speed in the unit given by <see cref="Unit"/>.
    /// </summary>
    public int Speed { get; init; }

    /// <summary>
    /// Maximum gust speed in the unit given by <see cref="Unit"/>,
    /// or <see langword="null"/> if no gust is reported.
    /// </summary>
    public int? Gust { get; init; }

    /// <summary>
    /// Speed unit: <c>"KT"</c> (knots) or <c>"MPS"</c> (meters per second).
    /// </summary>
    public string Unit { get; init; } = "KT";

    /// <summary>
    /// Start bearing (degrees true) of the variable wind sector (dndndn),
    /// or <see langword="null"/> if no variable-sector group follows the wind group.
    /// Only present when <see cref="IsVariable"/> is <see langword="false"/> and the
    /// total wind-direction variation exceeds 60° with a mean speed of 3 kt or more.
    /// </summary>
    public int? VariableFrom { get; init; }

    /// <summary>
    /// End bearing (degrees true) of the variable wind sector (dxdxdx),
    /// or <see langword="null"/> if no variable-sector group follows the wind group.
    /// Reported in clockwise order from <see cref="VariableFrom"/>.
    /// </summary>
    public int? VariableTo { get; init; }

    /// <summary>
    /// Returns the wind group in raw METAR notation,
    /// e.g. <c>"27015G25KT"</c> or <c>"27015KT 250V310"</c>.
    /// </summary>
    public override string ToString()
    {
        var dir = IsVariable ? "VRB" : Direction?.ToString("D3") ?? "///";
        var spd = $"{dir}{Speed:D2}";
        if (Gust.HasValue) spd += $"G{Gust:D2}";
        spd += Unit;
        if (VariableFrom.HasValue)
            spd += $" {VariableFrom:D3}V{VariableTo:D3}";
        return spd;
    }
}

/// <summary>
/// Decoded prevailing visibility group, corresponding to WMO 306 FM 15/16 §15.4.
/// Covers CAVOK, WMO meter-based values (0000–9999), and the US statute-mile format.
/// Optionally carries a minimum-visibility sub-group when the minimum differs from
/// the prevailing value and is less than 1 500 m.
/// </summary>
public sealed record Visibility
{
    /// <summary>
    /// <see langword="true"/> when the token <c>CAVOK</c> (Ceiling And Visibility OK)
    /// is present, indicating visibility of 10 km or more, no cloud below 5 000 ft,
    /// and no significant weather.  When <see langword="true"/> the weather and sky
    /// groups are omitted from the report.
    /// </summary>
    public bool Cavok { get; init; }

    /// <summary>
    /// Prevailing visibility in meters (WMO format), or <see langword="null"/> when
    /// <see cref="Cavok"/> is <see langword="true"/> or when the US statute-mile
    /// format is used instead.
    /// The value 9999 means 10 km or more; 0000 means less than 50 m.
    /// </summary>
    public int? DistanceMeters { get; init; }

    /// <summary>
    /// Prevailing visibility in statute miles (US METAR format, e.g. <c>10SM</c>,
    /// <c>1/2SM</c>, <c>1 3/4SM</c>), or <see langword="null"/> when the WMO
    /// meter format is used.
    /// </summary>
    public double? DistanceStatuteMiles { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <c>M</c> (less-than) prefix is present,
    /// indicating the actual visibility is below the reported value.
    /// </summary>
    public bool LessThan { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <c>NDV</c> suffix is appended, meaning no
    /// directional variation in visibility was observed.
    /// </summary>
    public bool NoDirectionalVariation { get; init; }

    /// <summary>
    /// Minimum visibility in meters, reported as a separate group when it differs
    /// from the prevailing visibility and is less than 1 500 m.
    /// <see langword="null"/> when not reported.
    /// </summary>
    public int? MinimumDistanceMeters { get; init; }

    /// <summary>
    /// Compass direction of the minimum visibility, one of:
    /// N, NE, E, SE, S, SW, W, NW.
    /// <see langword="null"/> when <see cref="MinimumDistanceMeters"/> is not reported.
    /// </summary>
    public string? MinimumDirection { get; init; }

    /// <summary>
    /// Returns the visibility in raw METAR notation, e.g. <c>"9999"</c> or <c>"CAVOK"</c>.
    /// Note: statute-mile values are not representable in WMO notation and are returned
    /// as the meter string with a zero distance.
    /// </summary>
    public override string ToString() =>
        Cavok ? "CAVOK" : $"{(LessThan ? "M" : "")}{DistanceMeters:D4}{(NoDirectionalVariation ? " NDV" : "")}";
}

/// <summary>
/// Decoded runway visual range (RVR) group, corresponding to WMO 306 FM 15/16 §15.5.
/// One group is reported per instrumented runway.
/// Format: R[runway]/[M|P][mean]i  or  R[runway]/[min]V[max]i
/// </summary>
public sealed class RunwayVisualRange
{
    /// <summary>
    /// Runway designator as it appears in the report, e.g. <c>"28L"</c>, <c>"09"</c>, <c>"36R"</c>.
    /// </summary>
    public string Runway { get; init; } = "";

    /// <summary>
    /// Mean RVR in meters when a single value is reported.
    /// <see langword="null"/> when a variable range is reported instead
    /// (see <see cref="MinMeters"/> and <see cref="MaxMeters"/>).
    /// </summary>
    public int? MeanMeters { get; init; }

    /// <summary>
    /// Lower bound of a variable RVR range in meters.
    /// <see langword="null"/> when a mean value is reported instead.
    /// </summary>
    public int? MinMeters { get; init; }

    /// <summary>
    /// Upper bound of a variable RVR range in meters.
    /// <see langword="null"/> when a mean value is reported instead.
    /// </summary>
    public int? MaxMeters { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <c>M</c> prefix is present, indicating the
    /// RVR is below the minimum measurable value of the instrument.
    /// </summary>
    public bool BelowMinimum { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <c>P</c> prefix is present, indicating the
    /// RVR is above the maximum measurable value of the instrument.
    /// </summary>
    public bool AboveMaximum { get; init; }

    /// <summary>
    /// One-character trend indicator appended to the group:
    /// <c>'U'</c> = upward, <c>'D'</c> = downward, <c>'N'</c> = no change.
    /// <see langword="null"/> when the trend is not reported.
    /// </summary>
    public char? Trend { get; init; }

    /// <summary>
    /// Returns the RVR group in raw METAR notation, e.g. <c>"R28L/0600N"</c>.
    /// </summary>
    public override string ToString()
    {
        var limit = BelowMinimum ? "M" : AboveMaximum ? "P" : "";
        var val = MinMeters.HasValue
            ? $"{MinMeters}V{MaxMeters}"
            : $"{limit}{MeanMeters:D4}";
        return $"R{Runway}/{val}{Trend}";
    }
}

/// <summary>
/// A single decoded present-weather or recent-weather phenomenon,
/// corresponding to WMO 306 FM 15/16 §15.6.
/// One token in the raw report may encode multiple simultaneous phenomena
/// (e.g. <c>TSRAGR</c> = thunderstorm with rain and hail).
/// Format: [+|-|VC][descriptor][precipitation][obscuration][other]
/// </summary>
public sealed class WeatherPhenomenon
{
    /// <summary>
    /// Intensity qualifier:
    /// <c>""</c> = moderate (no qualifier),
    /// <c>"-"</c> = light,
    /// <c>"+"</c> = heavy,
    /// <c>"VC"</c> = in the vicinity (within 8 km but not at the station).
    /// </summary>
    public string Intensity { get; init; } = "";

    /// <summary>
    /// Weather descriptor code, or <see langword="null"/> if absent.
    /// Valid codes: MI (shallow), PR (partial), BC (patches), DR (low drifting),
    /// BL (blowing), SH (shower), TS (thunderstorm), FZ (freezing).
    /// </summary>
    public string? Descriptor { get; init; }

    /// <summary>
    /// One or more precipitation type codes present in the token.
    /// Valid codes: DZ (drizzle), RA (rain), SN (snow), SG (snow grains),
    /// IC (ice crystals), PL (ice pellets), GR (hail),
    /// GS (small hail or snow pellets), UP (unknown precipitation).
    /// Empty when no precipitation type is encoded.
    /// </summary>
    public IReadOnlyList<string> Precipitation { get; init; } = [];

    /// <summary>
    /// Obscuration code, or <see langword="null"/> if absent.
    /// Valid codes: BR (mist), FG (fog), FU (smoke), VA (volcanic ash),
    /// DU (widespread dust), SA (sand), HZ (haze), PY (spray).
    /// </summary>
    public string? Obscuration { get; init; }

    /// <summary>
    /// Other weather phenomenon code, or <see langword="null"/> if absent.
    /// Valid codes: PO (dust/sand whirls), SQ (squalls),
    /// FC (funnel cloud / tornado / waterspout),
    /// SS (sandstorm), DS (duststorm).
    /// </summary>
    public string? Other { get; init; }

    /// <summary>
    /// Returns the phenomenon in compact raw METAR notation, e.g. <c>"-SHRA"</c>, <c>"TSRAGR"</c>.
    /// </summary>
    public override string ToString() =>
        Intensity +
        (Descriptor ?? "") +
        string.Concat(Precipitation) +
        (Obscuration ?? "") +
        (Other ?? "");
}

/// <summary>
/// A single decoded sky-condition or cloud layer group,
/// corresponding to WMO 306 FM 15/16 §15.7.
/// Multiple layers may be reported in a single report, in ascending height order.
/// </summary>
public sealed class SkyCondition
{
    /// <summary>
    /// Sky cover code.  One of:
    /// <c>FEW</c> (1–2 oktas), <c>SCT</c> (3–4 oktas), <c>BKN</c> (5–7 oktas),
    /// <c>OVC</c> (8 oktas / overcast), <c>VV</c> (vertical visibility — sky obscured),
    /// <c>SKC</c> / <c>CLR</c> (sky clear), <c>NSC</c> (no significant cloud),
    /// <c>NCD</c> (no cloud detected by automated system).
    /// </summary>
    public string Cover { get; init; } = "";

    /// <summary>
    /// Cloud base or vertical-visibility height in feet above aerodrome level,
    /// derived by multiplying the three-digit code value by 100.
    /// <see langword="null"/> for <c>SKC</c>, <c>CLR</c>, <c>NSC</c>, <c>NCD</c>,
    /// or when the height is unknown (<c>///</c>).
    /// </summary>
    public int? HeightFeet { get; init; }

    /// <summary>
    /// Significant cloud type appended to the layer group:
    /// <c>"CB"</c> (cumulonimbus) or <c>"TCU"</c> (towering cumulus).
    /// <see langword="null"/> when not present.
    /// </summary>
    public string? CloudType { get; init; }

    /// <summary>
    /// <see langword="true"/> when this group is a vertical-visibility (VV) group
    /// rather than a conventional cloud layer, indicating the sky is obscured and
    /// only the depth of the obscuring phenomenon is known.
    /// </summary>
    public bool IsVerticalVisibility { get; init; }

    /// <summary>
    /// Returns the sky-condition group in raw METAR notation, e.g. <c>"FEW030"</c>, <c>"BKN015CB"</c>.
    /// </summary>
    public override string ToString()
    {
        if (Cover is "SKC" or "CLR" or "NSC" or "NCD") return Cover;
        var h = HeightFeet.HasValue ? (HeightFeet.Value / 100).ToString("D3") : "///";
        return $"{Cover}{h}{CloudType}";
    }
}

/// <summary>
/// Decoded air and dew-point temperature group,
/// corresponding to WMO 306 FM 15/16 §15.8.
/// Format: TT/TdTd, where a leading <c>M</c> denotes a negative value.
/// </summary>
public sealed class Temperature
{
    /// <summary>Air temperature in degrees Celsius.</summary>
    public double Air { get; init; }

    /// <summary>
    /// Dew-point temperature in degrees Celsius.
    /// <see cref="double.NaN"/> when the dew point is missing from the report (<c>//</c>).
    /// </summary>
    public double DewPoint { get; init; }

    /// <summary>
    /// Returns the temperature group in raw METAR notation, e.g. <c>"10/05"</c> or <c>"M02/M08"</c>.
    /// </summary>
    public override string ToString()
    {
        static string Fmt(double v) => v < 0 ? $"M{Math.Abs(v):F0}" : $"{v:F0}";
        return $"{Fmt(Air)}/{Fmt(DewPoint)}";
    }
}

/// <summary>
/// Decoded altimeter / QNH setting group,
/// corresponding to WMO 306 FM 15/16 §15.9.
/// Supports both the ICAO/WMO Q-prefix (hectopascals) and the
/// US A-prefix (inches of mercury) formats.
/// </summary>
public sealed class Altimeter
{
    /// <summary>
    /// Altimeter setting value.
    /// When <see cref="Unit"/> is <c>"hPa"</c>, this is the QNH in whole hectopascals.
    /// When <see cref="Unit"/> is <c>"inHg"</c>, this is the altimeter setting in
    /// inches of mercury to two decimal places.
    /// </summary>
    public double Value { get; init; }

    /// <summary>
    /// Unit of the altimeter setting: <c>"hPa"</c> for a Q-prefix group,
    /// or <c>"inHg"</c> for an A-prefix group.
    /// </summary>
    public string Unit { get; init; } = "hPa";

    /// <summary>
    /// Returns the altimeter group in raw METAR notation,
    /// e.g. <c>"Q1013"</c> or <c>"A2992"</c>.
    /// </summary>
    public override string ToString() =>
        Unit == "hPa" ? $"Q{(int)Value:D4}" : $"A{(int)(Value * 100):D4}";
}

/// <summary>
/// A decoded TREND forecast appended to the body of a METAR or SPECI,
/// corresponding to WMO 306 FM 15/16 §15.12.
/// A trend describes expected changes to the meteorological conditions
/// within the next two hours.
/// </summary>
public sealed class TrendForecast
{
    /// <summary>
    /// Type of change forecast: <c>"BECMG"</c> (becoming — a permanent change),
    /// <c>"TEMPO"</c> (temporary — fluctuating conditions), or
    /// <c>"NOSIG"</c> (no significant changes expected).
    /// </summary>
    public string ChangeType { get; init; } = "";

    /// <summary>
    /// UTC time from which the change is expected, in HHmm format (FM indicator).
    /// <see langword="null"/> when not specified.
    /// </summary>
    public string? From { get; init; }

    /// <summary>
    /// UTC time until which the change is expected, in HHmm format (TL indicator).
    /// <see langword="null"/> when not specified.
    /// </summary>
    public string? Until { get; init; }

    /// <summary>
    /// Specific UTC time at which the change is expected to occur, in HHmm format (AT indicator).
    /// Used with BECMG when the change is expected at a precise time.
    /// <see langword="null"/> when not specified.
    /// </summary>
    public string? At { get; init; }

    /// <summary>
    /// Forecast wind conditions within the trend period,
    /// or <see langword="null"/> if wind is not forecast to change significantly.
    /// </summary>
    public Wind? Wind { get; init; }

    /// <summary>
    /// Forecast visibility within the trend period,
    /// or <see langword="null"/> if visibility is not forecast to change significantly.
    /// </summary>
    public Visibility? Visibility { get; init; }

    /// <summary>
    /// Forecast weather phenomena within the trend period.
    /// Empty when no significant weather change is forecast.
    /// </summary>
    public IReadOnlyList<WeatherPhenomenon> Weather { get; init; } = [];

    /// <summary>
    /// Forecast sky conditions within the trend period.
    /// Empty when no significant cloud change is forecast.
    /// </summary>
    public IReadOnlyList<SkyCondition> Sky { get; init; } = [];
}

/// <summary>
/// The complete decoded result of parsing a METAR or SPECI report string.
/// All optional fields are <see langword="null"/> or empty collections when
/// the corresponding group was absent from the report.
/// </summary>
public sealed class MetarReport
{
    /// <summary>The original, unmodified report string supplied to the parser.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Report type: <c>"METAR"</c> (routine observation) or
    /// <c>"SPECI"</c> (special observation issued outside the routine schedule).
    /// </summary>
    public string ReportType { get; init; } = "";

    /// <summary>
    /// Four-character station identifier of the observing station.
    /// True ICAO location indicators consist of four uppercase letters (e.g. <c>"EGLL"</c>),
    /// but some nations assign alphanumeric identifiers to smaller airports
    /// (e.g. <c>"K5T9"</c>, <c>"K1F0"</c>).
    /// </summary>
    public string Station { get; init; } = "";

    /// <summary>UTC day-of-month on which the observation was made (1–31).</summary>
    public int Day { get; init; }

    /// <summary>UTC hour of the observation time (0–23).</summary>
    public int Hour { get; init; }

    /// <summary>UTC minute of the observation time (0–59).</summary>
    public int Minute { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <c>AUTO</c> modifier is present, indicating
    /// the report was generated entirely by an automated observing system with no
    /// human intervention.
    /// </summary>
    public bool IsAuto { get; init; }

    /// <summary>
    /// <see langword="true"/> when the <c>COR</c> modifier is present, indicating
    /// this report is a correction of a previously issued report for the same time.
    /// </summary>
    public bool IsCorrection { get; init; }

    /// <summary>
    /// Decoded surface wind, or <see langword="null"/> if the wind group is absent.
    /// </summary>
    public Wind? Wind { get; init; }

    /// <summary>
    /// Decoded prevailing visibility, or <see langword="null"/> if the visibility
    /// group is absent.
    /// </summary>
    public Visibility? Visibility { get; init; }

    /// <summary>
    /// Runway visual range groups, one per instrumented runway.
    /// Empty when no RVR groups are present.
    /// </summary>
    public IReadOnlyList<RunwayVisualRange> Rvr { get; init; } = [];

    /// <summary>
    /// Present weather phenomena observed at the time of the report.
    /// Empty when no weather phenomena are reported.
    /// </summary>
    public IReadOnlyList<WeatherPhenomenon> PresentWeather { get; init; } = [];

    /// <summary>
    /// Sky condition and cloud layer groups, in ascending height order.
    /// Empty when CAVOK is reported or no sky-condition groups are present.
    /// </summary>
    public IReadOnlyList<SkyCondition> Sky { get; init; } = [];

    /// <summary>
    /// Air and dew-point temperature, or <see langword="null"/> if the group is absent.
    /// </summary>
    public Temperature? Temperature { get; init; }

    /// <summary>
    /// Altimeter / QNH setting, or <see langword="null"/> if the group is absent.
    /// </summary>
    public Altimeter? Altimeter { get; init; }

    /// <summary>
    /// Weather phenomena observed recently (within the past hour) but not at the
    /// time of the observation.  Encoded with the <c>RE</c> prefix in the raw report.
    /// Empty when no recent weather is reported.
    /// </summary>
    public IReadOnlyList<WeatherPhenomenon> RecentWeather { get; init; } = [];

    /// <summary>
    /// Wind-shear runway designators reported in <c>WS</c> groups.
    /// Contains <c>"ALL RWY"</c> when wind shear affects all runways, individual
    /// designators otherwise.  Empty when no wind-shear groups are present.
    /// </summary>
    public IReadOnlyList<string> WindShear { get; init; } = [];

    /// <summary>
    /// TREND forecast sections appended to the report.
    /// A report may contain NOSIG, one BECMG, one TEMPO, or a BECMG followed by a TEMPO.
    /// Empty when no trend section is present.
    /// </summary>
    public IReadOnlyList<TrendForecast> Trend { get; init; } = [];

    /// <summary>
    /// Free-text content following the <c>RMK</c> token, or <see langword="null"/>
    /// if no remarks section is present.
    /// </summary>
    public string? Remarks { get; init; }

    /// <summary>
    /// Tokens from the raw report that could not be matched to any known group.
    /// A non-empty list may indicate an unsupported extension or a malformed report.
    /// </summary>
    public IReadOnlyList<string> UnparsedGroups { get; init; } = [];
}
