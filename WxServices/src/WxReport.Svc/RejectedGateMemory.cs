// WX-506 rework: remembering a gate result that Claude has already rejected.
//
// On an unscheduled cycle the WX-114 gate compares the model's forecast (GFS with the TAF
// merged in) against the last sent report, and calls Claude when a criterion fires. Claude
// weighs all the evidence and may not adopt the model's change; the reconciled report then has
// no computed change and WX-506 withholds it. Nothing advances, so the next arrival fires the
// same criteria and pays for the same answer again. Measured 2026-09-29/30: Austin, TX fired
// `precip-remove@T1(09-30 11Z)` on 18 consecutive arrivals, each a full reconcile that was
// then withheld (WX-506 comments 16508, 16510).
//
// Claude's cheaper "not news" answer repeats the same way (from 2026-09-15 to 2026-09-30, 38 of 60
// repeated that locality's previous not-news criteria within 6 hours), so it is recorded too
// (Paul, 2026-09-30).
//
// So a withheld or not-news cycle records the criteria its gate fired, the evidence Claude
// weighed and the prior the gate measured against, and a later cycle within the window skips
// the Claude call when it asks nothing new: the gate fires only criteria on record, against the
// same prior, with the same GFS run in hand, and the METAR shows the same station, present
// weather, wind band and visibility band. A new GFS run always asks Claude again. A new TAF does
// not on its own: the gate's forecast merges the TAF in, so an amendment that matters fires a
// criterion not on record, or moves the prior, and Claude is asked. Measured 2026-10-01 in a
// slow, unsettled pattern: KIAH's TAF was amended six times in six hours, and all 7 re-asks a
// TAF alone caused got the same answer, while all 3 caused by a new GFS run sent (WX-506 comment
// 16564; Paul's decision, 2026-10-01). The clock can also bring new criteria (a block crossing
// into a nearer tier), and those are not on record, so Claude is asked. What the skip lets pass
// is a TAF amendment that changes nothing the gate fires, and a METAR that moved only in sky
// cover or temperature band; that is a judgment, not a proof that Claude would answer the same,
// and the window bounds it. Anything else asks Claude again. A delivered weather report clears
// the record; a prior that moved any other way turns it away.

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>Why a gate result was, or was not, treated as a repeat of a rejected one.</summary>
internal enum RejectedGateCheck
{
    /// <summary>Nothing on record, or the skip is turned off: ask Claude.</summary>
    NoRecord,

    /// <summary>Asks nothing new: skip the Claude call.</summary>
    Repeat,

    /// <summary>A criterion fired that is not on record.</summary>
    NewCriterion,

    /// <summary>A severe onset fired; always asked.</summary>
    SevereOnset,

    /// <summary>The last send no longer overlaps the horizon; always asked.</summary>
    DisjointHorizon,

    /// <summary>The record is as old as the window, or older.</summary>
    WindowExpired,

    /// <summary>The clock is behind the record's time.</summary>
    ClockBehind,

    /// <summary>A new GFS run has arrived since the rejection.  A new TAF alone does not reopen the question; it reaches Claude only by changing what the gate fires.</summary>
    NewGfsRun,

    /// <summary>The METAR station, its present weather, its wind band or its visibility band has changed since the rejection.</summary>
    NewWeather,

    /// <summary>The gate is measuring against a different prior than the one Claude's rejection was made against.</summary>
    NewBaseline,
}

/// <summary>
/// The WX-506 repeat skip: records, on the locality's state, the gate criteria, the input
/// identity and the prior behind an unscheduled cycle that Claude answered without a send
/// (withheld, or not news), and recognises a later cycle that asks nothing new. Pure functions over <see cref="LocalityState"/>; the caller persists the state.
/// </summary>
internal static class RejectedGateMemory
{
    /// <summary>The criterion the gate fires when the last send no longer overlaps the horizon.</summary>
    internal const string DisjointHorizonCriterion = "disjoint-horizon";

    /// <summary>
    /// The criteria as stored: distinct, sorted ordinally, one per line. <see langword="null"/>
    /// for an empty set, and for a set longer than the column, so the skip never acts on a
    /// truncated record.
    /// </summary>
    internal static string? Serialize(IEnumerable<string> criteria)
    {
        var set = criteria.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (set.Count == 0)
            return null;
        var text = string.Join('\n', set);
        return text.Length <= LocalityState.RejectedGateCriteriaMaxLength ? text : null;
    }

    /// <summary>
    /// Which withheld updates are recorded: a redundant one and one with no computed change,
    /// where Claude weighed the gate's change and did not adopt it. Not the severe-flag
    /// hysteresis, which withholds a change Claude did make.
    /// </summary>
    internal static bool Records(ReportWorker.UnscheduledSuppression suppression) =>
        suppression is ReportWorker.UnscheduledSuppression.Redundant
            or ReportWorker.UnscheduledSuppression.NoComputedChange;

    /// <summary>Record the gate result Claude has just answered without a send, the evidence and prior behind it, and <paramref name="nowUtc"/>.</summary>
    internal static void Record(LocalityState state, SignificanceResult gate, InputIdentity input, int priorSnapshotId, DateTime nowUtc)
    {
        state.LastRejectedGateCriteria = Serialize(gate.FiredCriteria);
        bool recorded = state.LastRejectedGateCriteria is not null;
        state.LastRejectedGateUtc = recorded ? nowUtc : null;
        state.LastRejectedInputHash = recorded ? input.Serialize() : null;
        state.LastRejectedPriorSnapshotId = recorded ? priorSnapshotId : null;
    }

    /// <summary>Forget any rejection on record: called when a weather report is delivered.</summary>
    internal static void Clear(LocalityState state)
    {
        state.LastRejectedGateCriteria = null;
        state.LastRejectedGateUtc = null;
        state.LastRejectedInputHash = null;
        state.LastRejectedPriorSnapshotId = null;
    }

    /// <summary>
    /// Whether this cycle's passing gate asks only what Claude has already answered, and if
    /// not, why. <see cref="RejectedGateCheck.Repeat"/> only when every fired criterion is on
    /// record, against the same prior, the record is younger than <paramref name="windowHours"/>,
    /// the same GFS run is in hand (a new TAF counts only through the criteria it fires), the METAR's station, present weather, wind band and
    /// visibility band are unchanged, and the result is neither a severe onset nor a disjoint
    /// horizon. Anything in doubt asks Claude.
    /// </summary>
    internal static RejectedGateCheck Check(
        SignificanceResult gate, LocalityState state, InputIdentity input, int priorSnapshotId, DateTime nowUtc, double windowHours)
    {
        if (windowHours <= 0 || !gate.Significant
            || state.LastRejectedGateCriteria is not { } recorded
            || state.LastRejectedGateUtc is not { } recordedUtc
            || state.LastRejectedInputHash is not { } recordedInput
            || state.LastRejectedPriorSnapshotId is not { } recordedPrior)
            return RejectedGateCheck.NoRecord;
        if (gate.SevereEntered)
            return RejectedGateCheck.SevereOnset;
        if (gate.FiredCriteria.Contains(DisjointHorizonCriterion))
            return RejectedGateCheck.DisjointHorizon;

        var age = nowUtc - recordedUtc;
        if (age < TimeSpan.Zero)
            return RejectedGateCheck.ClockBehind;
        if (age >= TimeSpan.FromHours(windowHours))
            return RejectedGateCheck.WindowExpired;

        if (recordedPrior != priorSnapshotId)
            return RejectedGateCheck.NewBaseline;

        var then = InputIdentity.Parse(recordedInput);
        if (then.Gfs != input.Gfs)
            return RejectedGateCheck.NewGfsRun;
        if (ObservedEvidence(then.Metar) != ObservedEvidence(input.Metar))
            return RejectedGateCheck.NewWeather;

        var known = recorded.Split('\n').ToHashSet(StringComparer.Ordinal);
        return gate.FiredCriteria.Count > 0 && gate.FiredCriteria.All(known.Contains)
            ? RejectedGateCheck.Repeat
            : RejectedGateCheck.NewCriterion;
    }

    /// <summary>
    /// The part of a METAR material signature (<c>STATION;W..;V..;S..;T..;P..</c>) whose change
    /// reopens a rejected question: the station, the wind band, the visibility band and the
    /// present-weather tokens. The sky and temperature bands are left out; they move hourly. The
    /// station is the first segment and is never read as a band, whatever letter it begins
    /// with. A signature without all three bands is returned whole, so any change counts.
    /// </summary>
    internal static string ObservedEvidence(string metarSignature)
    {
        var parts = metarSignature.Split(';');
        if (parts.Length < 2)
            return metarSignature;
        string? Band(char key) => parts.Skip(1).FirstOrDefault(p => p.Length > 0 && p[0] == key);
        return Band('W') is { } w && Band('V') is { } v && Band('P') is { } wx
            ? $"{parts[0]};{w};{v};{wx}"
            : metarSignature;
    }
}