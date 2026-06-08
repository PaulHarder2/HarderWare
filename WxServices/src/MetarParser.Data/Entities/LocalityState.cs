namespace MetarParser.Data.Entities;

/// <summary>
/// Per-locality reconciliation baseline and send cadence (WX-130).  Where the
/// retired <c>RecipientState</c> tracked these per recipient, the locality-batching
/// story (WX-123) moves the expensive Claude reconciliation to once per locality:
/// every member shares one schedule (WX-133) and one committed forecast, so the
/// baseline ("what did we last tell this locality, on what evidence, when") is a
/// property of the <see cref="Entities.Locality"/>, not of any one member.  The
/// per-recipient artifact that remains is the <see cref="CommittedSend"/> delivery
/// row (who received which render, when).
/// </summary>
public class LocalityState
{
    /// <summary>Auto-incremented surrogate key (bigint identity, project default).</summary>
    public long Id { get; set; }

    /// <summary>Foreign key to the <see cref="Entities.Locality"/> this state tracks; one row per locality.</summary>
    public long LocalityId { get; set; }

    /// <summary>Navigation to the locality.</summary>
    public Locality? Locality { get; set; }

    // ── reconciliation baseline ───────────────────────────────────────────────

    /// <summary>
    /// Serialised <c>InputIdentity</c> of the evidence handed to Claude at the
    /// locality's last reconciliation call (observation time, TAF issuance, GFS
    /// run).  The WX-80 pre-filter compares the current cycle against this; an exact
    /// match skips the Claude call without paying tokens.  <see langword="null"/>
    /// until the first reconciliation.
    /// </summary>
    public string? LastClaudeInputHash { get; set; }

    /// <summary>
    /// Serialised <c>InputIdentity</c> behind the locality's most recently
    /// <em>delivered</em> report (distinct from the last Claude call).  WX-108 uses
    /// it to tell Claude which inputs are genuinely fresh since the last delivery and
    /// to gate severe-flag hysteresis.  <see langword="null"/> until the first send.
    /// </summary>
    public string? LastSentInputHash { get; set; }

    /// <summary>
    /// ICAO of the METAR station behind the locality's most recent report.  Detects
    /// station switches within the locality's priority-ordered hierarchy (the primary
    /// had no recent data).  Only captured when an observation was available.
    /// </summary>
    public string? LastMetarIcao { get; set; }

    // ── send cadence (shared across the locality's members, WX-133) ────────────

    /// <summary>UTC time of the locality's most recent SCHEDULED report. <see langword="null"/> if never sent. The due-now check uses this to tell whether the current daily slot has been served.</summary>
    public DateTime? LastScheduledSentUtc { get; set; }

    /// <summary>UTC time of the locality's most recent UNSCHEDULED (change-triggered) report. <see langword="null"/> if never sent. Send-spacing (MinGapMinutes) uses Max(LastScheduledSentUtc, LastUnscheduledSentUtc).</summary>
    public DateTime? LastUnscheduledSentUtc { get; set; }
}