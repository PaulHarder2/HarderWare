namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>TafChangePeriodWeatherPhenomena</c> table.
/// Each row holds a single weather-phenomenon group within a TAF change period.
/// </summary>
public sealed class TafChangePeriodWeather
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key referencing the parent <see cref="TafChangePeriodRecord"/>.</summary>
    public int TafChangePeriodId { get; set; }

    /// <summary>Navigation property to the parent <see cref="TafChangePeriodRecord"/>.</summary>
    public TafChangePeriodRecord ChangePeriod { get; set; } = null!;

    /// <summary>
    /// Intensity qualifier: <c>""</c> (moderate), <c>"-"</c> (light),
    /// <c>"+"</c> (heavy), or <c>"VC"</c> (in the vicinity).
    /// </summary>
    public string Intensity { get; set; } = "";

    /// <summary>Weather descriptor code (MI, PR, BC, DR, BL, SH, TS, FZ), or <see langword="null"/>.</summary>
    public string? Descriptor { get; set; }

    /// <summary>
    /// Precipitation type codes as a comma-separated string (e.g. <c>"RA,SN"</c>),
    /// or <see langword="null"/> when absent.
    /// </summary>
    public string? Precipitation { get; set; }

    /// <summary>Obscuration code (BR, FG, FU, VA, DU, SA, HZ, PY), or <see langword="null"/>.</summary>
    public string? Obscuration { get; set; }

    /// <summary>Other phenomenon code (PO, SQ, FC, SS, DS), or <see langword="null"/>.</summary>
    public string? OtherPhenomenon { get; set; }

    /// <summary>Zero-based position within the change-period weather group list.</summary>
    public int SortOrder { get; set; }
}