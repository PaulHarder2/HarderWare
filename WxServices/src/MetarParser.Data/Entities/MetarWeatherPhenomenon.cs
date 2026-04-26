namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>MetarWeatherPhenomena</c> table.
/// Each row holds a single decoded weather-phenomenon group (w'w') from a
/// METAR or SPECI report, covering both present weather and recent weather
/// (RE-prefixed) groups.
/// Multiple rows may exist for the same <see cref="MetarRecord"/>.
/// </summary>
public sealed class MetarWeatherPhenomenon
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key referencing the parent <see cref="MetarRecord"/>.</summary>
    public int MetarId { get; set; }

    /// <summary>Navigation property to the parent <see cref="MetarRecord"/>.</summary>
    public MetarRecord Metar { get; set; } = null!;

    /// <summary>
    /// Indicates whether this phenomenon was observed at the time of the report
    /// or recently beforehand: <c>"Present"</c> or <c>"Recent"</c>.
    /// </summary>
    public string PhenomenonKind { get; set; } = "Present";

    /// <summary>
    /// Intensity qualifier: <c>""</c> (moderate), <c>"-"</c> (light),
    /// <c>"+"</c> (heavy), or <c>"VC"</c> (in the vicinity).
    /// </summary>
    public string Intensity { get; set; } = "";

    /// <summary>
    /// Weather descriptor code (MI, PR, BC, DR, BL, SH, TS, FZ),
    /// or <see langword="null"/> when absent.
    /// </summary>
    public string? Descriptor { get; set; }

    /// <summary>
    /// Precipitation type codes as a comma-separated string,
    /// e.g. <c>"RA"</c>, <c>"RA,SN"</c>, <c>"GR"</c>.
    /// <see langword="null"/> when no precipitation type is encoded.
    /// </summary>
    public string? Precipitation { get; set; }

    /// <summary>
    /// Obscuration code (BR, FG, FU, VA, DU, SA, HZ, PY),
    /// or <see langword="null"/> when absent.
    /// </summary>
    public string? Obscuration { get; set; }

    /// <summary>
    /// Other phenomenon code (PO, SQ, FC, SS, DS),
    /// or <see langword="null"/> when absent.
    /// </summary>
    public string? OtherPhenomenon { get; set; }

    /// <summary>
    /// Zero-based position of this phenomenon within its kind group,
    /// used to preserve the original report order when querying.
    /// </summary>
    public int SortOrder { get; set; }
}