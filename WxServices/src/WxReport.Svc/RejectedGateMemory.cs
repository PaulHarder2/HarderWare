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
// So a withheld or not-news cycle records the criteria its gate fired and the evidence Claude weighed, and
// a later cycle within the window skips the Claude call when it asks nothing new: the gate
// fires only criteria on record, and no new TAF, GFS run or change in observed weather has
// arrived since. The gate's forecast is built from the GFS run and the TAF alone, so with both
// unchanged its criteria, and their size, are the ones Claude already weighed; only the METAR
// has moved, and a METAR whose present weather is unchanged is what Claude already saw.
// Anything else asks Claude again. A send clears the record, because the baseline the
// criteria were measured against moves.

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

    /// <summary>A new TAF or GFS run has arrived since the rejection.</summary>
    NewGuidance,

    /// <summary>The METAR station or its present weather has changed since the rejection.</summary>
    NewWeather,
}

/// <summary>
/// The WX-506 repeat skip: records, on the locality's state, the gate criteria and the input
/// identity behind a withheld unscheduled update, and recognises a later cycle that asks
/// nothing new. Pure functions over <see cref="LocalityState"/>; the caller persists the state.
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

    /// <summary>Record the gate result whose reconciled update was just withheld, the evidence behind it, and <paramref name="nowUtc"/>.</summary>
    internal static void Record(LocalityState state, SignificanceResult gate, InputIdentity input, DateTime nowUtc)
    {
        state.LastRejectedGateCriteria = Serialize(gate.FiredCriteria);
        bool recorded = state.LastRejectedGateCriteria is not null;
        state.LastRejectedGateUtc = recorded ? nowUtc : null;
        state.LastRejectedInputHash = recorded ? input.Serialize() : null;
    }

    /// <summary>Forget any rejection on record: called whenever a delivery moves the locality's baseline.</summary>
    internal static void Clear(LocalityState state)
    {
        state.LastRejectedGateCriteria = null;
        state.LastRejectedGateUtc = null;
        state.LastRejectedInputHash = null;
    }

    /// <summary>
    /// Whether this cycle's passing gate asks only what Claude has already answered, and if
    /// not, why. <see cref="RejectedGateCheck.Repeat"/> only when every fired criterion is on
    /// record, the record is younger than <paramref name="windowHours"/>, the same TAF and GFS
    /// run are in hand, the METAR station and its present weather are unchanged, and the
    /// result is neither a severe onset nor a disjoint horizon. Anything in doubt asks Claude.
    /// </summary>
    internal static RejectedGateCheck Check(
        SignificanceResult gate, LocalityState state, InputIdentity input, DateTime nowUtc, double windowHours)
    {
        if (windowHours <= 0 || !gate.Significant
            || state.LastRejectedGateCriteria is not { } recorded
            || state.LastRejectedGateUtc is not { } recordedUtc
            || state.LastRejectedInputHash is not { } recordedInput)
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

        var then = InputIdentity.Parse(recordedInput);
        if (then.Taf != input.Taf || then.Gfs != input.Gfs)
            return RejectedGateCheck.NewGuidance;
        if (ObservedWeather(then.Metar) != ObservedWeather(input.Metar))
            return RejectedGateCheck.NewWeather;

        var known = recorded.Split('\n').ToHashSet(StringComparer.Ordinal);
        return gate.FiredCriteria.Count > 0 && gate.FiredCriteria.All(known.Contains)
            ? RejectedGateCheck.Repeat
            : RejectedGateCheck.NewCriterion;
    }

    /// <summary>
    /// The part of a METAR material signature that says what weather is happening: the station
    /// and its present-weather tokens. Wind, visibility, sky and temperature bands are left out:
    /// they move hourly and are what Claude already weighed the forecast against. A signature
    /// that does not have the expected shape is returned whole, so any change counts.
    /// </summary>
    internal static string ObservedWeather(string metarSignature)
    {
        var parts = metarSignature.Split(';');
        var weather = parts.FirstOrDefault(p => p.StartsWith('P'));
        return parts.Length > 1 && weather is not null ? $"{parts[0]};{weather}" : metarSignature;
    }
}