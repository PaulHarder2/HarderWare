// WX-527: a report Claude generated but email could not deliver is re-sent, not regenerated.
//
// From 2026-09-06 09:09Z to about 2026-09-07 05:00Z every SMTP send failed ("Network is
// unreachable") while Claude stayed reachable. Nothing was delivered, so nothing advanced, and every
// cycle generated the scheduled report again through Claude: 265 reconciles on 09-06 alone, about
// $35, 28% of the LLM bill for 2026-08-14..10-01 (WX-527).
//
// A failed send already leaves its CommittedSend rows unsent, carrying the rendered email. The
// latest-written reconciled report batch with unsent weather rows is the pending report, and those
// rows are the recipients still owed (which also covers a partial delivery). It is re-sent to them, with no
// Claude call, unless one of Paul's rules (2026-10-01, WX-527 comments 16577, 16579) says it is
// stale and must be regenerated:
//   - a severe block in it has come within the hazard horizon since it was generated;
//   - it is 3 hours old;
//   - a day-part boundary (00/06/12/18 local) has passed since it was generated, and it is a
//     scheduled report, or an unscheduled update with a change in the day-part that has ended;
//   - the evidence has moved on in substance: a new GFS run, a METAR changed in station, present
//     weather, wind band or visibility band, or a TAF that forecasts something different in
//     substance (the WX-506 tests).

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>Why a pending report is regenerated rather than re-sent, or <see cref="None"/> to re-send it.</summary>
internal enum PendingStale
{
    /// <summary>Not stale: re-send it.</summary>
    None,

    /// <summary>A severe block in it has come within the hazard horizon since it was generated.</summary>
    SevereOnset,

    /// <summary>It is <see cref="PendingDelivery.MaxAge"/> old, or the clock is behind it.</summary>
    TooOld,

    /// <summary>A day-part boundary has passed, and the report is scheduled or has a change in the ended day-part.</summary>
    DayPartCrossed,

    /// <summary>A new GFS run has arrived since it was generated.</summary>
    NewGfsRun,

    /// <summary>The METAR's station, present weather, wind band or visibility band has changed.</summary>
    NewWeather,

    /// <summary>A new TAF forecasts something different in substance, or could not be compared.</summary>
    NewTaf,

    /// <summary>The recorded evidence cannot be read back.</summary>
    UnreadableRecord,
}

/// <summary>The WX-527 staleness rules for a pending report.  Pure functions; the caller loads and sends.</summary>
internal static class PendingDelivery
{
    /// <summary>A pending report this old or older is regenerated (Paul, 2026-10-01).</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(3);

    /// <summary>Length of a day-part, matching the gate's local 6-hour blocks.</summary>
    private const int DayPartHours = 6;

    /// <summary>
    /// Whether the pending report is stale, and why, with the TAF assumed the same in substance.
    /// <paramref name="changeWindows"/> are the report's change windows (UTC); <paramref name="severeOnset"/>
    /// is the caller's hazard-horizon test.  <see cref="CheckAsync"/> adds the TAF comparison.
    /// </summary>
    internal static PendingStale Check(
        ReportKind kind, DateTime generatedUtc, string recordedInput, InputIdentity current,
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> changeWindows, bool severeOnset,
        DateTime nowUtc, TimeZoneInfo tz)
    {
        if (severeOnset)
            return PendingStale.SevereOnset;

        var age = nowUtc - generatedUtc;
        if (age < TimeSpan.Zero || age >= MaxAge)
            return PendingStale.TooOld;

        if (DayPartCrossed(kind, generatedUtc, changeWindows, nowUtc, tz))
            return PendingStale.DayPartCrossed;

        var then = InputIdentity.Parse(recordedInput);
        if (then.Serialize() != recordedInput)
            return PendingStale.UnreadableRecord;
        if (then.Gfs != current.Gfs)
            return PendingStale.NewGfsRun;
        if (RejectedGateMemory.ObservedEvidence(then.Metar) != RejectedGateMemory.ObservedEvidence(current.Metar))
            return PendingStale.NewWeather;
        return PendingStale.None;
    }

    /// <summary>
    /// The full decision: <see cref="Check"/>, then, only when it says re-send and the TAF's issuance
    /// differs from the recorded one, <paramref name="tafSameInSubstance"/> with the recorded issuance;
    /// a TAF that is not the same makes the report stale (<see cref="PendingStale.NewTaf"/>).
    /// </summary>
    internal static async Task<PendingStale> CheckAsync(
        ReportKind kind, DateTime generatedUtc, string recordedInput, InputIdentity current,
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> changeWindows, bool severeOnset,
        DateTime nowUtc, TimeZoneInfo tz, Func<string, Task<bool>> tafSameInSubstance)
    {
        var stale = Check(kind, generatedUtc, recordedInput, current, changeWindows, severeOnset, nowUtc, tz);
        if (stale != PendingStale.None)
            return stale;
        var recordedTaf = InputIdentity.Parse(recordedInput).Taf;
        if (recordedTaf == current.Taf)
            return PendingStale.None;
        return await tafSameInSubstance(recordedTaf) ? PendingStale.None : PendingStale.NewTaf;
    }

    /// <summary>
    /// Whether a day-part boundary (00/06/12/18 local) has passed since <paramref name="generatedUtc"/>
    /// in a way that makes the report stale: always for a scheduled report, whose first block has
    /// ended; for an unscheduled update, only when one of its changes lies in the day-part that has
    /// ended (Paul, 2026-10-01).
    /// </summary>
    internal static bool DayPartCrossed(
        ReportKind kind, DateTime generatedUtc, IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> changeWindows,
        DateTime nowUtc, TimeZoneInfo tz)
    {
        var thenStart = DayPartStartLocal(generatedUtc, tz);
        if (thenStart == DayPartStartLocal(nowUtc, tz))
            return false;
        if (kind != ReportKind.Unscheduled)
            return true;

        var endedFromUtc = ToUtc(thenStart, tz);
        var endedToUtc = ToUtc(thenStart.AddHours(DayPartHours), tz);
        return changeWindows.Any(w => w.StartUtc < endedToUtc && endedFromUtc < w.EndUtc);
    }

    /// <summary>
    /// Whether any severe block in the report has come within <paramref name="horizon"/> since it was
    /// generated: a block still active now, inside the horizon now, that was beyond it at generation.
    /// Per block, so a second severe block arriving counts even when another was already in range.
    /// </summary>
    internal static bool SevereOnsetSince(ForecastSnapshotBody body, DateTime generatedUtc, DateTime nowUtc, TimeSpan horizon) =>
        body.Blocks.Any(b =>
        {
            var start = DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc);
            return SevereBlocks.IsActive(b, nowUtc) && start <= nowUtc.Add(horizon) && start > generatedUtc.Add(horizon);
        });

    private static DateTime DayPartStartLocal(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.Date.AddHours(local.Hour / DayPartHours * DayPartHours);
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // WX-185 guard, as in NearTermCutoffUtc: in a zone that changes clocks at midnight, local
        // midnight on a spring-forward day does not exist and ConvertTimeToUtc would throw. Roll
        // forward an hour; it only shifts the edge of the ended day-part by that hour.
        if (tz.IsInvalidTime(wall))
            wall = wall.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(wall, tz);
    }

    /// <summary>
    /// The locality-state changes a WX-527 re-send makes, narrower than a fresh delivery's
    /// (<c>ReportWorker.ApplySentState</c>).  Only a report's <em>first</em> delivery
    /// (<paramref name="firstDelivery"/>: none of its rows was sent before) moves anything: the cadence
    /// stamp for its kind, the last-sent input identity to the report's own evidence, the METAR station
    /// to the report's, the last-Claude-call identity to the report's evidence unless a newer call is on
    /// record, and the WX-506 rejected-gate record, whose prior has moved.  A later re-send to recipients
    /// a partial delivery missed changes nothing.  It never touches the WX-182 degrade breaker (a cached
    /// re-send keeps it armed on purpose).
    /// </summary>
    internal static void ApplyResentState(
        LocalityState state, ReportKind kind, bool firstDelivery, DateTime nowUtc, string reportIdentity, string reportStation)
    {
        if (!firstDelivery)
            return;
        // The generating cycle called Claude on this evidence, but its failed send recorded nothing.
        // Record it now, unless a Claude call since the last delivery is already on record (the two
        // identities differ then), so the next cycle does not pay again for evidence this report covers.
        if (state.LastClaudeInputHash == state.LastSentInputHash)
            state.LastClaudeInputHash = reportIdentity;
        if (kind == ReportKind.Scheduled)
            state.LastScheduledSentUtc = nowUtc;
        else
            state.LastUnscheduledSentUtc = nowUtc;
        state.LastSentInputHash = reportIdentity;
        if (!string.IsNullOrEmpty(reportStation))
            state.LastMetarIcao = reportStation;
        RejectedGateMemory.Clear(state);
    }
}