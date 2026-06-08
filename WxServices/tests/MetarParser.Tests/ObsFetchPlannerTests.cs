using System.Collections.Generic;
using System.Linq;

using MetarParser.Data;

using WxServices.Common;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// Tests for the WX-140 observation fetch plan: small per-locality boxes
/// replacing the single CONUS bbox the AWC API silently truncated.  Pins the
/// geometry (box construction, latitude clamping), the ordering (home first),
/// and the containment dedupe that keeps overlapping localities from costing
/// redundant API calls.
/// </summary>
public class ObsFetchPlannerTests
{
    // Roughly the production shape on 2026-06-08: home near Houston, four
    // locality centroids (three TX within the home box, one OK outside it).
    private static readonly (double Lat, double Lon)[] ProductionLikePoints =
    [
        (30.07, -95.42),  // Spring, TX
        (30.29, -97.74),  // Austin, TX
        (35.86, -98.41),  // Watonga, OK
        (30.17, -96.40),  // Brenham, TX
    ];

    [Fact]
    public void HomeBoxFirst_PointsOutsideHomeGetTheirOwnBoxes()
    {
        var plan = ObsFetchPlanner.Plan(31.5, -97.0, 9.0, ProductionLikePoints, 2.0);

        // Home box ±9° around (31.5, -97) contains all three TX centroids and
        // ALSO Watonga (35.86, -98.41) — lat 22.5..40.5, lon -106..-88. So the
        // production-like plan collapses to the home box alone.
        Assert.Single(plan);
        Assert.Equal(new FetchRegion(22.5, 40.5, -106.0, -88.0), plan[0]);
    }

    [Fact]
    public void DistantPoint_GetsItsOwnBox()
    {
        var seattle = (47.6, -122.3);
        var plan = ObsFetchPlanner.Plan(31.5, -97.0, 9.0, [seattle], 2.0);

        Assert.Equal(2, plan.Count);
        Assert.Equal(new FetchRegion(45.6, 49.6, -124.3, -120.3), plan[1]);
    }

    [Fact]
    public void SmallHomeBox_DoesNotSwallowOklahoma()
    {
        // With a tighter home box (±3°), Watonga falls outside and earns its
        // own box — the exact coverage the truncated CONUS fetch never gave it.
        var plan = ObsFetchPlanner.Plan(31.5, -97.0, 3.0, ProductionLikePoints, 2.0);

        Assert.Contains(plan, box => box.Contains(35.86, -98.41)); // Watonga covered
        Assert.Contains(plan, box => box.Contains(36.34, -97.92)); // ...and KEND (Vance AFB) with it
    }

    [Fact]
    public void DuplicateAndContainedPoints_Deduplicate()
    {
        var plan = ObsFetchPlanner.Plan(null, null, 9.0,
            [(35.0, -98.0), (35.0, -98.0), (35.1, -98.1)], 2.0);

        // Identical points collapse; the slightly-offset third point's box is
        // NOT fully contained in the first (shifted corners), so it survives.
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void NoHome_PlanIsPointBoxesOnly()
    {
        var plan = ObsFetchPlanner.Plan(null, null, 9.0, [(35.86, -98.41)], 2.0);

        Assert.Single(plan);
        Assert.True(plan[0].Contains(35.86, -98.41));
    }

    [Fact]
    public void NothingConfigured_PlanIsEmpty()
    {
        Assert.Empty(ObsFetchPlanner.Plan(null, null, 9.0, [], 2.0));
    }

    [Fact]
    public void LatitudeClampsAtPoles()
    {
        var plan = ObsFetchPlanner.Plan(89.5, 0.0, 9.0, [], 2.0);

        Assert.Equal(90.0, plan[0].North);
        Assert.Equal(80.5, plan[0].South);
    }

    // ── MissingNeighborStations (the WX-140 gap fill) ─────────────────────────

    private static readonly (double Lat, double Lon) Watonga = (35.86, -98.41);

    private static readonly (string Icao, double Lat, double Lon)[] OkStations =
    [
        ("KJWG", 35.86, -98.41),  // Watonga Regional — at the centroid
        ("KEND", 36.34, -97.92),  // Vance AFB — ~68 km out, OUTSIDE the 50 km radius
        ("KCLK", 35.54, -98.93),  // Clinton Regional — ~57 km out, outside
        ("KRCE", 35.48, -97.82),  // ~68 km out, outside
        ("KOKC", 35.39, -97.60),  // Oklahoma City — ~90 km, well outside
    ];

    [Fact]
    public void NamedStationOutsideRadius_IsStillACandidate()
    {
        // The production case that motivated the named-station rule: Watonga's
        // own chosen station (KEND) sits outside the fallback radius of its
        // centroid. A pure radius sweep would never cover it.
        var gaps = ObsFetchPlanner.MissingNeighborStations(
            [Watonga], 50.0, OkStations,
            namedIcaos: ["KJWG", "KEND"],
            freshIcaos: []);

        Assert.Equal(["KEND", "KJWG"], gaps);
    }

    [Fact]
    public void RadiusNeighbors_AreCandidates_DistantStationsAreNot()
    {
        var gaps = ObsFetchPlanner.MissingNeighborStations(
            [Watonga], 60.0, OkStations, namedIcaos: [], freshIcaos: []);

        Assert.Contains("KJWG", gaps); // at the centroid
        Assert.Contains("KCLK", gaps); // ~57 km — inside 60
        Assert.DoesNotContain("KOKC", gaps); // ~90 km — outside
    }

    [Fact]
    public void FreshStations_AreNotGaps_CaseInsensitive()
    {
        var gaps = ObsFetchPlanner.MissingNeighborStations(
            [Watonga], 50.0, OkStations,
            namedIcaos: ["KJWG", "KEND"],
            freshIcaos: ["kjwg"]); // covered this cycle — lowercase on purpose

        Assert.Equal(["KEND"], gaps);
    }

    [Fact]
    public void NoPointsNoNames_NoGaps()
    {
        Assert.Empty(ObsFetchPlanner.MissingNeighborStations([], 50.0, OkStations, [], []));
    }

    // ── IcaoListFormat (the shared comma-list wire format) ───────────────────

    [Fact]
    public void IcaoListFormat_SplitsTrimsDedupes_DropsNoneCaseInsensitively()
    {
        var parsed = IcaoListFormat.Parse(["KDWH, KIAH", "kiah", null, "  ", "NONE", "KEND,none", "NONE,KJWG"]);

        Assert.Equal(["KDWH", "KIAH", "KEND", "KJWG"], parsed);
    }

    [Fact]
    public void IcaoListFormat_EmptyInput_YieldsEmpty()
    {
        Assert.Empty(IcaoListFormat.Parse([]));
        Assert.Empty(IcaoListFormat.Parse([null, "", "NONE"]));
    }

    [Fact]
    public void RegionContainsRegion_ExactAndPartialOverlap()
    {
        var outer = new FetchRegion(20, 40, -100, -80);

        Assert.True(outer.Contains(new FetchRegion(25, 35, -95, -85)));
        Assert.True(outer.Contains(outer)); // self-containment
        Assert.False(outer.Contains(new FetchRegion(25, 45, -95, -85))); // pokes out north
        Assert.False(outer.Contains(new FetchRegion(25, 35, -105, -85))); // pokes out west
    }
}