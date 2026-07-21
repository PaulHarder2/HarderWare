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
    /// locate the database, authenticate, and find the output and log directories
    /// without reading <c>config.json</c>.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string (parsed to extract server, database, and — for containerized deploys — the SQL login).</param>
    /// <param name="logsDir">Directory for Python log files.</param>
    public Dictionary<string, string> BuildPythonEnv(string connectionString, string logsDir)
    {
        // Parse with DbConnectionStringBuilder (BCL) rather than a naive ';'/'=' split, so a value
        // containing ';' or '=' (e.g. a quoted password) is decoded correctly. Lookups are
        // case-insensitive; synonyms are tried explicitly since the generic builder doesn't alias them.
        var csb = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        string? Value(params string[] keys)
        {
            foreach (var key in keys)
                if (csb.TryGetValue(key, out var v) && v is not null)
                    return v.ToString();
            return null;
        }

        var env = new Dictionary<string, string>
        {
            ["WXVIS_DB_SERVER"] = Value("Server", "Data Source") ?? @".\SQLEXPRESS",
            ["WXVIS_DB_NAME"] = Value("Database", "Initial Catalog") ?? "WeatherData",
            ["WXVIS_DB_DRIVER"] = DbDriver,
            ["WXVIS_OUTPUT_DIR"] = OutputDir,
            ["WXVIS_LOG_DIR"] = logsDir,
        };

        // SQL authentication: a Linux container has no Windows identity, so the connection
        // string must carry a SQL login for db.py to authenticate with UID/PWD. Both keys are
        // set together or not at all — the connection string stays the single source of truth
        // for the auth mode, mirroring the .NET side. If it carries no complete login, these
        // keys are omitted and db.py now FAILS CLOSED rather than falling back to
        // Trusted_Connection, which cannot work from a container (WX-329).
        var user = Value("User Id", "Uid");
        var password = Value("Password", "Pwd");
        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password))
        {
            env["WXVIS_DB_USER"] = user;
            env["WXVIS_DB_PASSWORD"] = password;
        }

        // Propagate the encryption posture so the connection string stays the single source of truth
        // for it (db.py applies these to the ODBC string; absent keys leave the driver's default).
        var encrypt = Value("Encrypt");
        if (!string.IsNullOrEmpty(encrypt))
            env["WXVIS_DB_ENCRYPT"] = encrypt;
        var trustCert = Value("TrustServerCertificate");
        if (!string.IsNullOrEmpty(trustCert))
            env["WXVIS_DB_TRUST_CERT"] = trustCert;

        return env;
    }
}