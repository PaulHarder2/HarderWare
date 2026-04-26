namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>TafChangePeriodSkyConditions</c> table.
/// Each row holds a single sky-condition layer within a TAF change period.
/// </summary>
public sealed class TafChangePeriodSky
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key referencing the parent <see cref="TafChangePeriodRecord"/>.</summary>
    public int TafChangePeriodId { get; set; }

    /// <summary>Navigation property to the parent <see cref="TafChangePeriodRecord"/>.</summary>
    public TafChangePeriodRecord ChangePeriod { get; set; } = null!;

    /// <summary>Sky cover code: FEW, SCT, BKN, OVC, VV, SKC, CLR, NSC, or NCD.</summary>
    public string Cover { get; set; } = "";

    /// <summary>Cloud base height in feet above aerodrome level, or <see langword="null"/>.</summary>
    public int? HeightFeet { get; set; }

    /// <summary>Significant cloud type: <c>"CB"</c> or <c>"TCU"</c>, or <see langword="null"/>.</summary>
    public string? CloudType { get; set; }

    /// <summary><see langword="true"/> when this row represents a vertical-visibility (VV) group.</summary>
    public bool IsVerticalVisibility { get; set; }

    /// <summary>Zero-based position within the change-period sky group list.</summary>
    public int SortOrder { get; set; }
}