namespace WxViewer;

/// <summary>Animation speed option shown in the speed selector ComboBox.</summary>
/// <param name="Label">Display name, e.g. "Slow".</param>
/// <param name="IntervalMs">Timer interval in milliseconds.</param>
public sealed record SpeedOption(string Label, int IntervalMs);
