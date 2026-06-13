using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// Shared predicates over severe (<see cref="ForecastSnapshotBlock.SevereFlag"/>) blocks,
/// extracted (WX-181) from the duplicated inline scans in the degrade path
/// (<see cref="ReportWorker"/>) and the hazard banner (<see cref="StructuredReportRenderer"/>).
/// The common kernel is <see cref="IsActive"/>: a severe block that has not fully elapsed
/// (its 6-hour window still reaches past <c>nowUtc</c>).
/// <para>
/// NOTE: a not-severe→severe <em>onset</em> is a different concept and lives in
/// <see cref="SignificanceGate"/> (the <c>severe-add</c> criterion / <c>SevereEntered</c>);
/// this class is about a severe block being <em>present/active</em>, not newly appearing.
/// </para>
/// </summary>
internal static class SevereBlocks
{
    // A snapshot block spans 6 hours from its start (the GFS/TAF block grid).
    private const int BlockHours = 6;

    /// <summary>True when <paramref name="b"/> is flagged severe and has not fully elapsed at <paramref name="nowUtc"/> (start + 6 h still in the future).</summary>
    internal static bool IsActive(ForecastSnapshotBlock b, DateTime nowUtc) =>
        b.SevereFlag && DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc).AddHours(BlockHours) > nowUtc;

    /// <summary>The earliest still-active severe block, or <see langword="null"/> when none — the hazard banner leads with this.</summary>
    internal static ForecastSnapshotBlock? EarliestActive(ForecastSnapshotBody body, DateTime nowUtc) =>
        body.Blocks
            .Where(b => IsActive(b, nowUtc))
            .OrderBy(b => b.StartUtc)
            .FirstOrDefault();

    /// <summary>True when at least one still-active severe block starts within <paramref name="horizon"/> of <paramref name="nowUtc"/> — the degrade path's "hazard soon, send the narrative-less alert anyway" test.</summary>
    internal static bool AnyActiveWithin(ForecastSnapshotBody body, DateTime nowUtc, TimeSpan horizon) =>
        body.Blocks.Any(b => IsActive(b, nowUtc)
            && DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc) <= nowUtc.Add(horizon));
}