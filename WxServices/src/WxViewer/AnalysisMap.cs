namespace WxViewer;

/// <summary>
/// Represents one synoptic analysis PNG file found on disk.
/// </summary>
/// <param name="ObsUtc">The observation time embedded in the filename.</param>
/// <param name="FilePath">Absolute path to the PNG file.</param>
/// <param name="Label">Human-readable label shown in the ComboBox.</param>
public sealed record AnalysisMap(DateTime ObsUtc, string FilePath, string Label);
