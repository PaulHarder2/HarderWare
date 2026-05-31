namespace MetarParser.Data.Entities;

/// <summary>
/// Persists the report-delivery state for a single recipient.
/// Tracks the last time a scheduled report was sent and the last time an
/// unscheduled (significant-change) report was sent, along with a fingerprint
/// of the snapshot at the time of the most recent send.  Used by WxReport.Svc
/// to decide whether a new report should be sent.
/// </summary>
public class RecipientState
{
    public int Id { get; set; }

    /// <summary>Stable recipient identifier matching <see cref="Recipient.RecipientId"/> in the <c>Recipients</c> table.</summary>
    public string RecipientId { get; set; } = "";

    /// <summary>UTC time the most recent scheduled report was sent, or null if never sent.</summary>
    public DateTime? LastScheduledSentUtc { get; set; }

    /// <summary>UTC time the most recent unscheduled (change-triggered) report was sent, or null if never sent.</summary>
    public DateTime? LastUnscheduledSentUtc { get; set; }

    /// <summary>
    /// Vestigial since WX-80.  Held the pre-WX-80 8-field
    /// <c>SnapshotFingerprint</c> string used for observation-to-observation
    /// change detection; that mechanism (and the fingerprint type) was removed
    /// when triggers were unified behind the Claude invalidation gate.  The
    /// column is left in place for WX-83 to drop as part of the migration plan;
    /// nothing reads or writes it now.
    /// </summary>
    public string? LastSnapshotFingerprint { get; set; }

    /// <summary>
    /// Serialised <c>InputIdentity</c> of the evidence handed to Claude at the
    /// last reconciliation call (METAR observation time, TAF issuance, GFS model
    /// run).  The WX-80 pre-filter compares the current cycle's identity against
    /// this value: an exact match means no input has advanced since the last
    /// Claude call, so the call is skipped without paying tokens.  Null until the
    /// first Claude call for the recipient.
    /// </summary>
    public string? LastClaudeInputHash { get; set; }

    /// <summary>
    /// ICAO of the METAR station used for the most recent report sent to this recipient.
    /// Used to detect station switches caused by the primary station having no recent data,
    /// so Claude can include context explaining the change in observation source.
    /// </summary>
    public string? LastMetarIcao { get; set; }
}