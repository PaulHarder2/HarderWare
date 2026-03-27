namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>MetarSkyConditions</c> table.
/// Each row holds a single decoded sky-condition or cloud-layer group
/// from a METAR or SPECI report.
/// Multiple rows may exist for the same <see cref="MetarRecord"/>,
/// ordered by <see cref="SortOrder"/> to preserve the original layer sequence.
/// </summary>
public sealed class MetarSkyCondition
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key referencing the parent <see cref="MetarRecord"/>.</summary>
    public int MetarId { get; set; }

    /// <summary>Navigation property to the parent <see cref="MetarRecord"/>.</summary>
    public MetarRecord Metar { get; set; } = null!;

    /// <summary>
    /// Sky cover code: <c>FEW</c>, <c>SCT</c>, <c>BKN</c>, <c>OVC</c>, <c>VV</c>,
    /// <c>SKC</c>, <c>CLR</c>, <c>NSC</c>, or <c>NCD</c>.
    /// </summary>
    public string Cover { get; set; } = "";

    /// <summary>
    /// Cloud base or vertical-visibility height in feet above aerodrome level.
    /// <see langword="null"/> for <c>SKC</c>, <c>CLR</c>, <c>NSC</c>, <c>NCD</c>,
    /// or when the height is unknown.
    /// </summary>
    public int? HeightFeet { get; set; }

    /// <summary>
    /// Significant cloud type: <c>"CB"</c> (cumulonimbus) or <c>"TCU"</c> (towering cumulus).
    /// <see langword="null"/> when not reported.
    /// </summary>
    public string? CloudType { get; set; }

    /// <summary>
    /// <see langword="true"/> when this row represents a vertical-visibility (VV) group
    /// rather than a conventional cloud layer.
    /// </summary>
    public bool IsVerticalVisibility { get; set; }

    /// <summary>
    /// Zero-based position of this layer within the report, used to restore the
    /// original ascending-height order when querying child rows.
    /// </summary>
    public int SortOrder { get; set; }
}
