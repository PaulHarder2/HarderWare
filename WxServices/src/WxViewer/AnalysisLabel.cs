namespace WxViewer;

/// <summary>
/// Groups all synoptic analysis frames sharing the same region label,
/// ordered oldest-to-newest for animation playback.
/// </summary>
/// <param name="Name">The raw label from the filename, e.g. "south_central".</param>
/// <param name="Frames">All frames for this label, sorted oldest-first.</param>
/// <param name="Label">Human-readable ComboBox display string.</param>
public sealed record AnalysisLabel(string Name, List<AnalysisMap> Frames, string Label);
