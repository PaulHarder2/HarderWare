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
    public int      Id                       { get; set; }

    /// <summary>Stable recipient identifier matching <see cref="Recipient.RecipientId"/> in the <c>Recipients</c> table.</summary>
    public string   RecipientId              { get; set; } = "";

    /// <summary>UTC time the most recent scheduled report was sent, or null if never sent.</summary>
    public DateTime? LastScheduledSentUtc    { get; set; }

    /// <summary>UTC time the most recent unscheduled (change-triggered) report was sent, or null if never sent.</summary>
    public DateTime? LastUnscheduledSentUtc  { get; set; }

    /// <summary>
    /// Compact fingerprint of the weather snapshot at the time of the last send.
    /// A change in fingerprint value indicates a significant weather change has occurred.
    /// </summary>
    public string?  LastSnapshotFingerprint  { get; set; }
}
