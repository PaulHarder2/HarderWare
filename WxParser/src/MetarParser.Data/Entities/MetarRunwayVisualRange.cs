namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>MetarRunwayVisualRanges</c> table.
/// Each row holds a single decoded RVR group from a METAR or SPECI report.
/// Multiple rows may exist for the same <see cref="MetarRecord"/>,
/// one per instrumented runway.
/// </summary>
public sealed class MetarRunwayVisualRange
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key referencing the parent <see cref="MetarRecord"/>.</summary>
    public int MetarId { get; set; }

    /// <summary>Navigation property to the parent <see cref="MetarRecord"/>.</summary>
    public MetarRecord Metar { get; set; } = null!;

    /// <summary>
    /// Runway designator as it appears in the report, e.g. <c>"28L"</c>, <c>"09"</c>.
    /// </summary>
    public string Runway { get; set; } = "";

    /// <summary>
    /// Mean RVR in meters when a single value is reported.
    /// <see langword="null"/> when a variable range is reported instead.
    /// </summary>
    public int? MeanMeters { get; set; }

    /// <summary>
    /// Lower bound of a variable RVR range in meters.
    /// <see langword="null"/> when a mean value is reported instead.
    /// </summary>
    public int? MinMeters { get; set; }

    /// <summary>
    /// Upper bound of a variable RVR range in meters.
    /// <see langword="null"/> when a mean value is reported instead.
    /// </summary>
    public int? MaxMeters { get; set; }

    /// <summary>
    /// <see langword="true"/> when the M prefix was present, indicating the RVR
    /// is below the minimum measurable value of the instrument.
    /// </summary>
    public bool BelowMinimum { get; set; }

    /// <summary>
    /// <see langword="true"/> when the P prefix was present, indicating the RVR
    /// is above the maximum measurable value of the instrument.
    /// </summary>
    public bool AboveMaximum { get; set; }

    /// <summary>
    /// Trend indicator: <c>"U"</c> (upward), <c>"D"</c> (downward),
    /// <c>"N"</c> (no change), or <see langword="null"/> when not reported.
    /// </summary>
    public string? Trend { get; set; }
}
