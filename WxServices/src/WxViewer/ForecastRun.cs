namespace WxViewer;

/// <summary>
/// One frame within a GFS model run.
/// </summary>
/// <param name="ForecastHour">The integer forecast hour (e.g. 24).</param>
/// <param name="ValidUtc">The valid time = ModelRunUtc + ForecastHour hours.</param>
/// <param name="FilePath">Absolute path to the PNG file.</param>
/// <param name="HourLabel">Short label shown in the status bar, e.g. "+024h  Valid: 2026-04-03 18Z".</param>
public sealed record ForecastFrame(int ForecastHour, DateTime ValidUtc, string FilePath, string HourLabel);

/// <summary>
/// Represents one GFS model run, grouping all its forecast-hour PNG files.
/// </summary>
/// <param name="ModelRunUtc">The model initialisation time.</param>
/// <param name="Frames">Ordered list of available forecast frames for this run.</param>
/// <param name="Label">Human-readable label shown in the ComboBox, e.g. "GFS  2026-04-02 18Z".</param>
public sealed record ForecastRun(DateTime ModelRunUtc, List<ForecastFrame> Frames, string Label);
