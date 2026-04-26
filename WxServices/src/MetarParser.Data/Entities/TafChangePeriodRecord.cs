namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>TafChangePeriods</c> table.
/// Each row holds a single change group (BECMG, TEMPO, FM, PROB30, PROB40,
/// PROB30 TEMPO, or PROB40 TEMPO) from a TAF, including its forecast wind,
/// visibility, and references to child sky and weather rows.
/// </summary>
public sealed class TafChangePeriodRecord
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key referencing the parent <see cref="TafRecord"/>.</summary>
    public int TafId { get; set; }

    /// <summary>Navigation property to the parent <see cref="TafRecord"/>.</summary>
    public TafRecord Taf { get; set; } = null!;

    /// <summary>
    /// Change indicator: <c>"BASE"</c> (the initial forecast period),
    /// <c>"BECMG"</c>, <c>"TEMPO"</c>, <c>"FM"</c>,
    /// <c>"PROB30"</c>, <c>"PROB40"</c>, <c>"PROB30 TEMPO"</c>,
    /// or <c>"PROB40 TEMPO"</c>.
    /// </summary>
    public string ChangeType { get; set; } = "";

    /// <summary>Inferred UTC start of this change period, or <see langword="null"/> if not determinable.</summary>
    public DateTime? ValidFromUtc { get; set; }

    /// <summary>Inferred UTC end of this change period (BECMG/TEMPO/PROB only), or <see langword="null"/>.</summary>
    public DateTime? ValidToUtc { get; set; }

    // ── wind ────────────────────────────────────────────────────────────────

    /// <summary>Wind direction in degrees true, or <see langword="null"/> when unchanged or absent.</summary>
    public int? WindDirection { get; set; }

    /// <summary><see langword="true"/> when the direction was reported as VRB.</summary>
    public bool WindIsVariable { get; set; }

    /// <summary>Mean wind speed, or <see langword="null"/> when wind is not reported for this period.</summary>
    public int? WindSpeed { get; set; }

    /// <summary>Gust speed, or <see langword="null"/> when no gust is reported.</summary>
    public int? WindGust { get; set; }

    /// <summary>Wind speed unit: <c>"KT"</c> or <c>"MPS"</c>, or <see langword="null"/> when absent.</summary>
    public string? WindUnit { get; set; }

    // ── visibility ───────────────────────────────────────────────────────────

    /// <summary><see langword="true"/> when <c>CAVOK</c> was reported in this period.</summary>
    public bool VisibilityCavok { get; set; }

    /// <summary>Prevailing visibility in meters, or <see langword="null"/> when absent or CAVOK.</summary>
    public int? VisibilityM { get; set; }

    /// <summary>Prevailing visibility in statute miles, or <see langword="null"/> when absent.</summary>
    public double? VisibilityStatuteMiles { get; set; }

    /// <summary><see langword="true"/> when the visibility was prefixed with M (less-than).</summary>
    public bool VisibilityLessThan { get; set; }

    // ── raw multi-value strings ──────────────────────────────────────────────

    /// <summary>
    /// Sky condition tokens for this period joined into a single string,
    /// or <see langword="null"/> when absent.
    /// </summary>
    public string? RawSkyConditions { get; set; }

    /// <summary>
    /// Weather phenomenon tokens for this period joined into a single string,
    /// or <see langword="null"/> when absent.
    /// </summary>
    public string? RawWeather { get; set; }

    // ── position ─────────────────────────────────────────────────────────────

    /// <summary>Zero-based position of this change period within the TAF, preserving order.</summary>
    public int SortOrder { get; set; }

    // ── navigation properties ─────────────────────────────────────────────────

    /// <summary>Structured sky condition rows for this change period.</summary>
    public List<TafChangePeriodSky> SkyConditions { get; set; } = [];

    /// <summary>Structured weather phenomenon rows for this change period.</summary>
    public List<TafChangePeriodWeather> WeatherPhenomena { get; set; } = [];
}