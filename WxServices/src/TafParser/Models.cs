// Models for parsed TAF (Terminal Aerodrome Forecast) reports.
// Based on WMO Manual 306, Volume I.1, FM 51 TAF.
//
// TAF shares several token types with METAR — Wind, Visibility,
// WeatherPhenomenon, and SkyCondition are reused from the MetarParser assembly.

using MetarParser;

namespace TafParser;

/// <summary>
/// A fully decoded TAF (Terminal Aerodrome Forecast) report.
/// </summary>
public sealed class TafReport
{
    /// <summary>The original, unmodified TAF string passed to the parser.</summary>
    public string Raw { get; init; } = "";

    /// <summary>
    /// Report type: <c>"TAF"</c>, <c>"TAF AMD"</c> (amendment),
    /// or <c>"TAF COR"</c> (correction).
    /// </summary>
    public string ReportType { get; init; } = "";

    /// <summary>ICAO aerodrome identifier, e.g. <c>"EGLL"</c>.</summary>
    public string Station { get; init; } = "";

    /// <summary>Day of month (UTC) the forecast was issued.</summary>
    public int IssuanceDay { get; init; }

    /// <summary>Hour (UTC) the forecast was issued.</summary>
    public int IssuanceHour { get; init; }

    /// <summary>Minute (UTC) the forecast was issued.</summary>
    public int IssuanceMinute { get; init; }

    /// <summary>Day of month (UTC) the validity period starts.</summary>
    public int ValidFromDay { get; init; }

    /// <summary>Hour (UTC) the validity period starts.</summary>
    public int ValidFromHour { get; init; }

    /// <summary>Day of month (UTC) the validity period ends.</summary>
    public int ValidToDay { get; init; }

    /// <summary>Hour (UTC) the validity period ends.</summary>
    public int ValidToHour { get; init; }

    /// <summary>Prevailing wind for the base forecast period, or <see langword="null"/> if absent.</summary>
    public Wind? Wind { get; init; }

    /// <summary>Prevailing visibility for the base forecast period, or <see langword="null"/> if absent.</summary>
    public Visibility? Visibility { get; init; }

    /// <summary>Forecast weather phenomena for the base period.</summary>
    public List<WeatherPhenomenon> Weather { get; init; } = [];

    /// <summary>Forecast sky conditions for the base period.</summary>
    public List<SkyCondition> Sky { get; init; } = [];

    /// <summary>Change periods (BECMG, TEMPO, PROB, FM) in the order they appear.</summary>
    public List<TafChangePeriod> ChangePeriods { get; init; } = [];

    /// <summary>Tokens the parser could not decode.</summary>
    public List<string> UnparsedGroups { get; init; } = [];
}

/// <summary>
/// A single change period within a TAF — one of BECMG, TEMPO, FM, PROB30,
/// PROB40, PROB30 TEMPO, or PROB40 TEMPO.
/// </summary>
public sealed class TafChangePeriod
{
    /// <summary>
    /// Change indicator: <c>"BECMG"</c>, <c>"TEMPO"</c>, <c>"FM"</c>,
    /// <c>"PROB30"</c>, <c>"PROB40"</c>, <c>"PROB30 TEMPO"</c>,
    /// or <c>"PROB40 TEMPO"</c>.
    /// </summary>
    public string ChangeType { get; init; } = "";

    /// <summary>
    /// Day of month (UTC) the change period starts,
    /// or <see langword="null"/> for BECMG/TEMPO periods where the
    /// period is encoded in <see cref="ValidityGroup"/>.
    /// </summary>
    public int? FromDay { get; init; }

    /// <summary>Hour (UTC) the change period starts.</summary>
    public int? FromHour { get; init; }

    /// <summary>Day of month (UTC) the change period ends (BECMG/TEMPO only).</summary>
    public int? ToDay { get; init; }

    /// <summary>Hour (UTC) the change period ends (BECMG/TEMPO only).</summary>
    public int? ToHour { get; init; }

    /// <summary>Forecast wind for this period, or <see langword="null"/> if unchanged.</summary>
    public Wind? Wind { get; init; }

    /// <summary>Forecast visibility for this period, or <see langword="null"/> if unchanged.</summary>
    public Visibility? Visibility { get; init; }

    /// <summary>Forecast weather phenomena for this period.</summary>
    public List<WeatherPhenomenon> Weather { get; init; } = [];

    /// <summary>Forecast sky conditions for this period.</summary>
    public List<SkyCondition> Sky { get; init; } = [];
}
