namespace WxViewer;

/// <summary>
/// Represents a single synoptic analysis PNG file available for display.
/// </summary>
/// <param name="Name">Absolute file path — used as a stable identity key for selection preservation.</param>
/// <param name="Frames">Single-element list containing the one <see cref="AnalysisMap"/> for this file.</param>
/// <param name="Label">Human-readable ComboBox display string, e.g. "2026-04-03 06Z".</param>
public sealed record AnalysisLabel(string Name, List<AnalysisMap> Frames, string Label);
