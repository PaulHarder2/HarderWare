namespace WxViewer;

/// <summary>
/// Represents one GFS model run for which meteogram PNGs have been rendered,
/// as discovered from a manifest JSON file in the plots directory.
/// </summary>
/// <param name="ModelRunUtc">UTC timestamp of the GFS model run.</param>
/// <param name="Label">Human-readable run label (e.g. "GFS 2026-04-04 00Z").</param>
/// <param name="Items">Meteogram items for each recipient location, sorted by ICAO.</param>
public record MeteogramRun(DateTime ModelRunUtc, string Label, List<MeteogramItem> Items);