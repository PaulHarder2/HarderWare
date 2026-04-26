namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>Tafs</c> table.
/// Each row is the header record for a single decoded TAF: identifiers,
/// issuance and validity times, and the raw text.  All forecast content
/// (base period and change periods) is stored as child rows in
/// <c>TafChangePeriods</c>, with the base period carried as
/// <c>ChangeType = "BASE"</c> at <c>SortOrder = 0</c>.
/// </summary>
public sealed class TafRecord
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Report type: <c>"TAF"</c>, <c>"TAF AMD"</c>, or <c>"TAF COR"</c>.
    /// </summary>
    public string ReportType { get; set; } = "";

    /// <summary>Four-letter ICAO station identifier, e.g. <c>"EGLL"</c>.</summary>
    public string StationIcao { get; set; } = "";

    /// <summary>
    /// Inferred UTC date and time at which the forecast was issued.
    /// </summary>
    public DateTime IssuanceUtc { get; set; }

    /// <summary>Inferred UTC start of the forecast validity period.</summary>
    public DateTime ValidFromUtc { get; set; }

    /// <summary>Inferred UTC end of the forecast validity period.</summary>
    public DateTime ValidToUtc { get; set; }

    /// <summary>The complete original TAF string exactly as received.</summary>
    public string RawReport { get; set; } = "";

    /// <summary>UTC date and time at which this row was inserted into the database.</summary>
    public DateTime ReceivedUtc { get; set; }

    // ── navigation properties ─────────────────────────────────────────────────

    /// <summary>
    /// All forecast periods: element 0 is the base period
    /// (<c>ChangeType = "BASE"</c>); subsequent elements are the change groups
    /// (BECMG, TEMPO, FM, PROB) in the order they appear in the TAF.
    /// </summary>
    public List<TafChangePeriodRecord> ChangePeriods { get; set; } = [];
}