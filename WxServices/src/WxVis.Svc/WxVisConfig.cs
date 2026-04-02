namespace WxVis.Svc;

/// <summary>
/// Configuration model for WxVis.Svc.
/// Bound from the <c>WxVis</c> section of appsettings files at runtime.
/// </summary>
public class WxVisConfig
{
    /// <summary>Full path to the Python executable in the wxvis conda environment.</summary>
    public string CondaPythonExe { get; set; } =
        @"C:\Users\PaulH\miniconda3\envs\wxvis\python.exe";

    /// <summary>Directory containing the WxVis Python scripts.</summary>
    public string ScriptDir { get; set; } =
        @"C:\Users\PaulH\Dropbox\PH\Documents\Code\HarderWare\WxServices\src\WxVis";

    /// <summary>Directory where rendered PNG maps are written (must match WxVis config.json output_dir).</summary>
    public string OutputDir { get; set; } = @"C:\HarderWare\plots";

    /// <summary>Minutes past the UTC hour at which the synoptic analysis map is generated.</summary>
    public int AnalysisMapMinutePastHour { get; set; } = 10;

    /// <summary>Arguments passed to synoptic_map.py for automatic renders (e.g. "--extent south_central").</summary>
    public string SynopticMapArgs { get; set; } = "--extent south_central";

    /// <summary>How often the forecast map worker polls the database for new forecast hours (seconds).</summary>
    public int ForecastPollIntervalSeconds { get; set; } = 30;

    /// <summary>Number of days to retain PNG plot files in <see cref="OutputDir"/> before deleting them.</summary>
    public int PlotRetentionDays { get; set; } = 14;
}
