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
// So a withheld cycle records the criteria its gate fired, and a later cycle within the
// window whose gate fires only those criteria skips the Claude call. Anything new still asks:
// a criterion not on record, a severe onset, a disjoint horizon, or the window running out.
// A send clears the record, because the baseline the criteria were measured against moves.

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// The WX-506 repeat skip: records, on the locality's state, the gate criteria behind a
/// withheld unscheduled update, and recognises a later gate result that asks nothing new.
/// Pure functions over <see cref="LocalityState"/>; the caller persists the state.
/// </summary>
internal static class RejectedGateMemory
{
    /// <summary>The criterion the gate fires when the last send no longer overlaps the horizon; never skipped, since there is nothing Claude compared against.</summary>
    internal const string DisjointHorizon = "disjoint-horizon";

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

    /// <summary>Record the criteria of the gate result whose reconciled update was just withheld, stamped <paramref name="nowUtc"/>.</summary>
    internal static void Record(LocalityState state, SignificanceResult gate, DateTime nowUtc)
    {
        state.LastRejectedGateCriteria = Serialize(gate.FiredCriteria);
        state.LastRejectedGateUtc = state.LastRejectedGateCriteria is null ? null : nowUtc;
    }

    /// <summary>Forget any rejection on record: called whenever a report is sent.</summary>
    internal static void Clear(LocalityState state)
    {
        state.LastRejectedGateCriteria = null;
        state.LastRejectedGateUtc = null;
    }

    /// <summary>
    /// True when this cycle's passing gate asks only what Claude has already answered: every
    /// fired criterion is on record, the record is younger than <paramref name="windowHours"/>,
    /// and the result is neither a severe onset nor a disjoint horizon. False whenever there is
    /// anything to doubt, so the fallback is always to ask Claude.
    /// </summary>
    internal static bool IsRepeat(SignificanceResult gate, LocalityState state, DateTime nowUtc, double windowHours)
    {
        if (!gate.Significant || gate.SevereEntered || gate.FiredCriteria.Count == 0 || windowHours <= 0)
            return false;
        if (state.LastRejectedGateCriteria is not { } recorded || state.LastRejectedGateUtc is not { } recordedUtc)
            return false;

        var age = nowUtc - recordedUtc;
        if (age < TimeSpan.Zero || age >= TimeSpan.FromHours(windowHours))
            return false;   // a clock that went backwards, or a window that has run out: ask again

        var known = recorded.Split('\n').ToHashSet(StringComparer.Ordinal);
        return gate.FiredCriteria.All(c => c != DisjointHorizon && known.Contains(c));
    }
}