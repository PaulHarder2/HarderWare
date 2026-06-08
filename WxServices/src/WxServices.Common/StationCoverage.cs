namespace WxServices.Common;

/// <summary>
/// The constants that bind the report-side station fallback to the fetch-side
/// coverage guarantee (WX-140).  The whole gap-fill design rests on one
/// invariant: <em>every station the fallback is allowed to choose must be a
/// station the fetch layer attempted</em> — so the radius and the freshness
/// window live here once, referenced by both layers, instead of as twin
/// literals tied together by a comment.
/// </summary>
public static class StationCoverage
{
    /// <summary>
    /// Maximum distance (km) the report-side fallback may roam from the
    /// recipient for a substitute station — and therefore the neighborhood
    /// radius the fetch-side gap fill must sweep.  50 km ≈ 30 statute miles.
    /// </summary>
    public const double MaxFallbackDistanceKm = 50.0;

    /// <summary>
    /// How recent an observation must be to count as "current" — for the
    /// fallback's station eligibility and for the gap fill's "already covered"
    /// test alike.
    /// </summary>
    public static readonly TimeSpan FreshObservationWindow = TimeSpan.FromHours(3);
}