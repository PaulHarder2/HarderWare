namespace MetarParser.Data.Entities;

/// <summary>
/// One row per report-send to one recipient.  Anchors the send to the
/// <see cref="ForecastSnapshot"/> that was committed at the moment the
/// decision to send was made, and carries the artifacts produced by the
/// Claude pass — the structured reasoning trace and the rendered email
/// body — so cycles have a diff anchor and an audit trail.
///
/// Introduced under WX-78 as part of the WX-47 rearchitecture.  The row is
/// written provisional before Claude is invoked (so a Claude failure still
/// leaves a record of what we were about to send), then overwritten on
/// Claude success with the body and trace.  <see cref="SentAtUtc"/> stays
/// null until the email actually goes out, which cleanly distinguishes
/// Claude-completed-but-not-sent rows from sent ones.
/// </summary>
public sealed class CommittedSend
{
    /// <summary>Current schema version for the column shape on this row.</summary>
    public const int SchemaVersionCurrent = 1;

    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="ForecastSnapshot"/> this send was anchored
    /// against.  Non-nullable: every send commits against a snapshot, even when
    /// the snapshot is the WX-78 placeholder produced before WX-77 lands the
    /// real <c>GfsSnapshotBuilder</c>.
    /// </summary>
    public int ForecastSnapshotId { get; set; }

    /// <summary>Navigation property to the anchored snapshot.</summary>
    public ForecastSnapshot ForecastSnapshot { get; set; } = null!;

    /// <summary>Stable recipient identifier matching <see cref="Recipient.RecipientId"/>.</summary>
    public string RecipientId { get; set; } = "";

    /// <summary>
    /// Claude's structured reasoning trace for this send, or <see langword="null"/>
    /// in the provisional row and when Claude failed.  Population is WX-79's
    /// responsibility once tool-use returns are wired; WX-78 reserves the column.
    /// </summary>
    public string? ReasoningTrace { get; set; }

    /// <summary>
    /// The rendered HTML email body returned by Claude, or <see langword="null"/>
    /// in the provisional row and when Claude failed.  Filled on Claude success.
    /// </summary>
    public string? EmailBody { get; set; }

    /// <summary>UTC time the provisional row was first written, before Claude was invoked.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// UTC time the email was actually accepted by the SMTP relay, or
    /// <see langword="null"/> if the row never reached the send phase
    /// (Claude failed, send failed, or the cycle aborted earlier).
    /// </summary>
    public DateTime? SentAtUtc { get; set; }

    /// <summary>Schema version of the column shape on this row.  Defaults to <see cref="SchemaVersionCurrent"/>.</summary>
    public int SchemaVersion { get; set; } = SchemaVersionCurrent;
}