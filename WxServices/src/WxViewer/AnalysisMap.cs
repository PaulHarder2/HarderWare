namespace WxViewer;

/// <summary>
/// Represents one synoptic analysis observation time with PNG files at each zoom level.
/// </summary>
/// <param name="ObsUtc">The observation time embedded in the filename.</param>
/// <param name="FilePath">Absolute path to the z1 (base zoom) PNG file.</param>
/// <param name="Label">Human-readable label shown in the ComboBox.</param>
/// <param name="ZoomPaths">Zoom level → absolute file path mapping (1-based).</param>
public sealed record AnalysisMap(
    DateTime ObsUtc,
    string FilePath,
    string Label,
    IReadOnlyDictionary<int, string> ZoomPaths);
