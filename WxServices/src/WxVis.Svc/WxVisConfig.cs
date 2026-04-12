using WxServices.Common;

namespace WxVis.Svc;

/// <summary>
/// Configuration model for WxVis.Svc.
/// Bound from the <c>WxVis</c> section of appsettings files at runtime.
/// </summary>
public class WxVisConfig
{
    /// <summary>Full path to the Python executable in the wxvis conda environment.</summary>
    public string CondaPythonExe { get; set; } = "";

    /// <summary>Minutes past the UTC hour at which the synoptic analysis map is generated.</summary>
    public int AnalysisMapMinutePastHour { get; set; } = 10;

    /// <summary>
    /// Map extent for rendered maps.  Can be a preset name (e.g. <c>"south_central"</c>,
    /// <c>"conus"</c>) or explicit W,E,S,N coordinates (e.g. <c>"-106,-88,25,38"</c>).
    /// When empty or null, maps auto-fit to the available data.
    /// </summary>
    public string MapExtent { get; set; } = "";

    /// <summary>How often the forecast map worker polls the database for new forecast hours (seconds).</summary>
    public int ForecastPollIntervalSeconds { get; set; } = 30;

    /// <summary>Number of days to retain PNG plot files before deleting them.</summary>
    public int PlotRetentionDays { get; set; } = 14;

    /// <summary>
    /// Number of zoom levels to render for analysis and forecast maps.
    /// Each successive level doubles the scale factor (and image size).
    /// Level 1 is the base; level 2 is 2x, level 3 is 4x, etc.
    /// </summary>
    public int ZoomLevels { get; set; } = 3;

    /// <summary>ODBC driver name for the Python DB connection.</summary>
    public string DbDriver { get; set; } = "ODBC Driver 17 for SQL Server";

    // ── Derived from InstallRoot (not bound from config) ─���───────────────────

    /// <summary>Directory containing the WxVis Python scripts. Set by <see cref="ApplyPaths"/>.</summary>
    public string ScriptDir { get; private set; } = "";

    /// <summary>Directory where rendered PNG maps are written. Set by <see cref="ApplyPaths"/>.</summary>
    public string OutputDir { get; private set; } = "";

    /// <summary>
    /// Sets the derived path properties from the given <see cref="WxPaths"/> instance.
    /// Call this after <c>IConfiguration.Bind</c>.
    /// </summary>
    public void ApplyPaths(WxPaths paths)
    {
        ScriptDir = paths.WxVisDir;
        OutputDir = paths.PlotsDir;
    }

    /// <summary>
    /// Builds environment variables passed to WxVis Python scripts so they can
    /// locate the database, output directory, and log directory without reading
    /// <c>config.json</c>.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string (parsed to extract server and database).</param>
    /// <param name="logsDir">Directory for Python log files.</param>
    public Dictionary<string, string> BuildPythonEnv(string connectionString, string logsDir)
    {
        // Parse "Server=.\SQLEXPRESS;Database=WeatherData;..." into components.
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);

        return new Dictionary<string, string>
        {
            ["WXVIS_DB_SERVER"]   = parts.GetValueOrDefault("Server", @".\SQLEXPRESS"),
            ["WXVIS_DB_NAME"]     = parts.GetValueOrDefault("Database", "WeatherData"),
            ["WXVIS_DB_DRIVER"]   = DbDriver,
            ["WXVIS_OUTPUT_DIR"]  = OutputDir,
            ["WXVIS_LOG_DIR"]     = logsDir,
        };
    }
}
