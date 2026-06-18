using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// Shared predicates over severe (<see cref="ForecastSnapshotBlock.SevereFlag"/>) blocks,
/// extracted (WX-181) from the duplicated inline scans in the degrade path
/// (<see cref="ReportWorker"/>) and the hazard banner (<see cref="StructuredReportRenderer"/>).
/// The common kernel is <see cref="NotFullyElapsed"/> (a block whose 6-hour window still
/// reaches past <c>nowUtc</c>); <see cref="IsActive"/> layers the severe-flag test on top,
/// and the WX-188 day-grid trim (<see cref="StructuredReportRenderer"/>) reuses the kernel
/// directly so the grid and the hazard banner agree on what "past" means.
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

    /// <summary>
    /// True when block <paramref name="b"/>'s 6-hour window has not fully elapsed at
    /// <paramref name="nowUtc"/> (start + 6 h still in the future) — the shared "still in
    /// play" kernel, severe or not. The WX-188 Extended-Forecast day-grid trim reuses this
    /// so a day is dropped only once all of its blocks are past.
    /// </summary>
    internal static bool NotFullyElapsed(ForecastSnapshotBlock b, DateTime nowUtc) =>
        DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc).AddHours(BlockHours) > nowUtc;

    /// <summary>True when <paramref name="b"/> is flagged severe and has not fully elapsed at <paramref name="nowUtc"/> (start + 6 h still in the future).</summary>
    internal static bool IsActive(ForecastSnapshotBlock b, DateTime nowUtc) =>
        b.SevereFlag && NotFullyElapsed(b, nowUtc);

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