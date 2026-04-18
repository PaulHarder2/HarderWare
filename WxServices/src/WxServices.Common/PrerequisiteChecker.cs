using System.Diagnostics;
using Microsoft.Data.SqlClient;
using WxServices.Logging;

namespace WxServices.Common;

/// <summary>
/// Result of a single prerequisite check.
/// </summary>
/// <param name="Ok">Whether the check passed.</param>
/// <param name="Message">Human-readable status (e.g. version string on success, error on failure).</param>
public sealed record CheckResult(bool Ok, string Message);

/// <summary>
/// Checks system prerequisites required by the WxServices components.
/// Each method is independent and can be called individually — services
/// check only the prerequisites they need, while WxManager checks all of them.
/// </summary>
public static class PrerequisiteChecker
{
    /// <summary>
    /// Tests whether SQL Server is reachable at the given connection string.
    /// </summary>
    public static async Task<CheckResult> CheckSqlServerAsync(string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            var version = (string?)await new SqlCommand("SELECT @@VERSION", conn).ExecuteScalarAsync();
            // Extract just the first line (product name + version).
            var shortVersion = version?.Split('\n', 2)[0].Trim() ?? "connected";
            return new CheckResult(true, shortVersion);
        }
        catch (Exception ex)
        {
            return new CheckResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Tests whether the <c>WeatherData</c> database exists and is accessible.
    /// </summary>
    public static async Task<CheckResult> CheckDatabaseAsync(string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            var dbName = conn.Database;
            return new CheckResult(true, $"Database '{dbName}' accessible.");
        }
        catch (Exception ex)
        {
            return new CheckResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Tests whether the native Windows wgrib2 binary exists and runs.
    /// wgrib2 returns a non-zero exit code (typically 8) even for <c>--version</c>,
    /// so we accept any output containing a digit as a pass.
    /// </summary>
    /// <remarks>
    /// WX-33 replaced the previous WSL-invoked probe with a direct
    /// <c>wgrib2.exe</c> launch.  Virtual service accounts (<c>NT SERVICE\*</c>)
    /// have no WSL distro; the native build works under any identity.
    /// </remarks>
    public static async Task<CheckResult> CheckWgrib2Async(string wgrib2Path)
    {
        if (string.IsNullOrWhiteSpace(wgrib2Path))
            return new CheckResult(false, "Gfs:Wgrib2Path is not configured.");
        if (!File.Exists(wgrib2Path))
            return new CheckResult(false, $"File not found: {wgrib2Path}");

        var result = await RunProcessAsync(wgrib2Path, "--version", "wgrib2");
        if (!result.Ok && result.Message.Any(char.IsDigit))
            return new CheckResult(true, $"wgrib2 {result.Message}");
        return result;
    }

    /// <summary>
    /// Tests whether the conda Python executable exists on disk.
    /// </summary>
    public static CheckResult CheckCondaPython(string pythonExePath)
    {
        if (string.IsNullOrWhiteSpace(pythonExePath))
            return new CheckResult(false, "WxVis:CondaPythonExe is not configured.");

        return File.Exists(pythonExePath)
            ? new CheckResult(true, pythonExePath)
            : new CheckResult(false, $"File not found: {pythonExePath}");
    }

    /// <summary>
    /// Tests whether key wxvis Python packages (cartopy, matplotlib, metpy) are
    /// importable in the configured conda environment.
    /// </summary>
    /// <remarks>
    /// The conda Python executable requires its environment's DLL directories on
    /// PATH to load native extensions.  This method augments PATH the same way
    /// <c>MapRenderer</c> does.
    /// </remarks>
    public static async Task<CheckResult> CheckWxVisPackagesAsync(string pythonExePath)
    {
        if (!File.Exists(pythonExePath))
            return new CheckResult(false, $"Python not found: {pythonExePath}");

        var condaEnvDir  = Path.GetDirectoryName(pythonExePath)!;
        var extraEnv = new Dictionary<string, string>
        {
            ["PATH"] = string.Join(';',
                condaEnvDir,
                Path.Combine(condaEnvDir, "Library", "bin"),
                Path.Combine(condaEnvDir, "Scripts"),
                Environment.GetEnvironmentVariable("PATH") ?? ""),
        };

        return await RunProcessAsync(
            pythonExePath,
            "-c \"import cartopy, matplotlib, metpy, sqlalchemy; print('OK')\"",
            "wxvis packages",
            extraEnv);
    }

    /// <summary>
    /// Tests whether Docker is running by executing <c>docker info</c>.
    /// </summary>
    public static async Task<CheckResult> CheckDockerAsync()
    {
        return await RunProcessAsync("docker", "info --format '{{.ServerVersion}}'", "Docker");
    }

    // ── Startup logging ────────────────────────────────────────────────────

    /// <summary>
    /// Which prerequisites a service requires.
    /// </summary>
    /// <remarks>
    /// The <c>Wsl</c> flag was retired in WX-33 when the GFS pipeline switched
    /// from WSL-invoked <c>wgrib2</c> to the native Windows build.  The
    /// bit-pattern values are preserved so old config cannot collide.
    /// </remarks>
    [Flags]
    public enum Requires
    {
        None         = 0,
        SqlServer    = 1 << 0,
        Wgrib2       = 1 << 2,
        CondaPython  = 1 << 3,
        WxVisPackages = 1 << 4,
        Docker       = 1 << 5,
    }

    /// <summary>
    /// Runs the prerequisite checks appropriate for a service and logs the
    /// results.  Failures are logged at WARN — this method never throws or
    /// blocks startup.
    /// </summary>
    /// <param name="requires">Which checks to run.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="wgrib2Path">Absolute Windows path to wgrib2.exe.</param>
    /// <param name="condaPythonExe">Path to the conda Python executable.</param>
    public static async Task LogPrerequisitesAsync(
        Requires requires,
        string connectionString = "",
        string wgrib2Path = "",
        string condaPythonExe = "")
    {
        var checks = new List<(string Label, Func<Task<CheckResult>> Check)>();

        if (requires.HasFlag(Requires.SqlServer))
            checks.Add(("SQL Server", () => CheckSqlServerAsync(connectionString)));
        if (requires.HasFlag(Requires.Wgrib2))
            checks.Add(("wgrib2", () => CheckWgrib2Async(wgrib2Path)));
        if (requires.HasFlag(Requires.CondaPython))
            checks.Add(("Conda Python", () => Task.FromResult(CheckCondaPython(condaPythonExe))));
        if (requires.HasFlag(Requires.WxVisPackages))
            checks.Add(("wxvis packages", () => CheckWxVisPackagesAsync(condaPythonExe)));
        if (requires.HasFlag(Requires.Docker))
            checks.Add(("Docker", CheckDockerAsync));

        foreach (var (label, check) in checks)
        {
            try
            {
                var result = await check();
                if (result.Ok)
                    Logger.Info($"Prerequisite OK: {label} — {result.Message}");
                else
                    Logger.Warn($"Prerequisite FAILED: {label} — {result.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Prerequisite check error: {label} — {ex.Message}");
            }
        }
    }

    // ── helper ───────────────────────────────────────────────────────────────

    private static async Task<CheckResult> RunProcessAsync(
        string fileName, string arguments, string label,
        IDictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = fileName,
                Arguments              = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            if (env is not null)
                foreach (var (key, value) in env)
                    psi.Environment[key] = value;

            using var proc = Process.Start(psi);
            if (proc is null)
                return new CheckResult(false, $"Failed to start {label} process.");

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                var output = stdout.Trim();
                if (string.IsNullOrEmpty(output)) output = $"{label} OK";
                // Truncate long output to first line.
                var firstLine = output.Split('\n', 2)[0].Trim();
                return new CheckResult(true, firstLine);
            }

            var errorMsg = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
            return new CheckResult(false, string.IsNullOrEmpty(errorMsg) ? $"{label} exited with code {proc.ExitCode}" : errorMsg);
        }
        catch (Exception ex)
        {
            return new CheckResult(false, $"{label}: {ex.Message}");
        }
    }
}
