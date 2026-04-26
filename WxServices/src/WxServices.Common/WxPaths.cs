using System.Reflection;
using System.Text.Json;

namespace WxServices.Common;

/// <summary>
/// Derives all standard directory paths from a single <see cref="InstallRoot"/>.
/// Every path used by any WxServices component should be obtained from this
/// class rather than hardcoded, so that the entire installation is relocatable.
/// </summary>
public sealed class WxPaths
{
    /// <summary>
    /// Reads <c>InstallRoot</c> from <c>appsettings.shared.json</c> in the
    /// application's base directory.  Returns <see cref="DefaultInstallRoot"/>
    /// if the file or property is missing.
    /// </summary>
    /// <remarks>
    /// Call this early in startup — before the configuration builder runs — so
    /// that the <see cref="InstallRoot"/> is available to set up the
    /// <c>PhysicalFileProvider</c> for <c>appsettings.local.json</c>.
    /// </remarks>
    public static string ReadInstallRoot()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.shared.json");
        if (!File.Exists(path)) return DefaultInstallRoot;

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("InstallRoot", out var prop))
                return prop.GetString() ?? DefaultInstallRoot;
        }
        catch { /* fall through to default */ }

        return DefaultInstallRoot;
    }

    /// <summary>Default installation root used when the setting is absent from configuration.</summary>
    public const string DefaultInstallRoot = @"C:\HarderWare";

    /// <summary>
    /// Returns the WxServices product version string (e.g. "1.0.0") from the
    /// calling assembly's informational version, or the assembly version as fallback.
    /// </summary>
    public static string ProductVersion =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Returns the short git commit hash embedded at build time, or "unknown" if unavailable.
    /// </summary>
    public static string GitCommit
    {
        get
        {
            var full = Assembly.GetEntryAssembly()?
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "GitCommit")?.Value;
            if (string.IsNullOrWhiteSpace(full)) return "unknown";
            return full.Length > 7 ? full[..7] : full;
        }
    }

    /// <summary>Root directory for the entire WxServices installation.</summary>
    public string InstallRoot { get; }

    /// <summary>Directory for all log files and heartbeat files.</summary>
    public string LogsDir { get; }

    /// <summary>Directory for rendered PNG maps and meteograms.</summary>
    public string PlotsDir { get; }

    /// <summary>Working directory for temporary GFS GRIB2 / CSV files.</summary>
    public string TempDir { get; }

    /// <summary>Directory containing the WxVis Python scripts.</summary>
    public string WxVisDir { get; }

    /// <summary>Directory for published Windows service binaries.</summary>
    public string ServicesDir { get; }

    /// <summary>Directory for the WxManager GUI application.</summary>
    public string WxManagerDir { get; }

    /// <summary>Directory for the WxViewer desktop application.</summary>
    public string WxViewerDir { get; }

    /// <summary>Path to the shared log4net configuration file.</summary>
    public string Log4NetConfigPath { get; }

    /// <summary>Path to <c>appsettings.local.json</c> in the install root (optional override layer).</summary>
    public string LocalConfigPath { get; }

    /// <summary>Path to <c>announce.txt</c> in the install root.</summary>
    public string AnnounceFilePath { get; }

    /// <summary>Default Windows path to the native <c>wgrib2.exe</c> binary, derived from <see cref="InstallRoot"/>.</summary>
    /// <remarks>
    /// Resolves to <c>{InstallRoot}\wgrib2\wgrib2.exe</c>.  WX-33 replaced the
    /// WSL-invoked <c>wgrib2</c> (previously bundled under <c>tools/</c>) with
    /// the NOAA native Windows build so services running under
    /// <c>NT SERVICE\*</c> virtual accounts can invoke it without a per-user
    /// WSL distro.
    /// </remarks>
    public string Wgrib2DefaultPath { get; }

    /// <summary>Creates a new <see cref="WxPaths"/> instance with all paths derived from <paramref name="installRoot"/>.</summary>
    /// <param name="installRoot">
    /// The installation root directory.  Pass <see langword="null"/> or empty to use <see cref="DefaultInstallRoot"/>.
    /// </param>
    public WxPaths(string? installRoot = null)
    {
        InstallRoot = string.IsNullOrWhiteSpace(installRoot) ? DefaultInstallRoot : installRoot;

        LogsDir = Path.Combine(InstallRoot, "Logs");
        PlotsDir = Path.Combine(InstallRoot, "plots");
        TempDir = Path.Combine(InstallRoot, "temp");
        WxVisDir = Path.Combine(InstallRoot, "WxVis");
        ServicesDir = Path.Combine(InstallRoot, "services");
        WxManagerDir = Path.Combine(InstallRoot, "WxManager");
        WxViewerDir = Path.Combine(InstallRoot, "WxViewer");
        Log4NetConfigPath = Path.Combine(InstallRoot, "log4net.shared.config");
        LocalConfigPath = Path.Combine(InstallRoot, "appsettings.local.json");
        AnnounceFilePath = Path.Combine(InstallRoot, "announce.txt");
        Wgrib2DefaultPath = Path.Combine(InstallRoot, "wgrib2", "wgrib2.exe");
    }

    /// <summary>Returns the heartbeat file path for a given service name.</summary>
    public string HeartbeatFile(string serviceName)
        => Path.Combine(LogsDir, $"{serviceName}-heartbeat.txt");

    /// <summary>Returns the log file path for a given service name.</summary>
    public string LogFile(string serviceName)
        => Path.Combine(LogsDir, $"{serviceName}.log");
}