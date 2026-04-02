using System.Diagnostics;
using WxServices.Logging;

namespace WxVis.Svc;

/// <summary>
/// Shells out to the wxvis conda environment's Python interpreter to run a
/// WxVis script, capturing stdout and stderr to the service log.
/// </summary>
public static class MapRenderer
{
    /// <summary>
    /// Runs <paramref name="scriptName"/> with the given <paramref name="args"/>
    /// using the configured conda Python executable.
    /// </summary>
    /// <param name="pythonExe">Full path to the conda environment Python executable.</param>
    /// <param name="scriptDir">Directory containing the WxVis Python scripts.</param>
    /// <param name="scriptName">Script file name (e.g. <c>"forecast_map.py"</c>).</param>
    /// <param name="args">Command-line arguments to pass to the script (e.g. <c>"--fh 24"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the process exited with code 0; otherwise <see langword="false"/>.</returns>
    /// <sideeffects>
    /// Starts a child process.  Captures stdout and stderr to the log.
    /// </sideeffects>
    public static async Task<bool> RunAsync(
        string pythonExe,
        string scriptDir,
        string scriptName,
        string args,
        CancellationToken ct = default)
    {
        var scriptPath = Path.Combine(scriptDir, scriptName);

        if (!File.Exists(pythonExe))
        {
            Logger.Error($"MapRenderer: Python executable not found: {pythonExe}");
            return false;
        }
        if (!File.Exists(scriptPath))
        {
            Logger.Error($"MapRenderer: Script not found: {scriptPath}");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName               = pythonExe,
            Arguments              = $"\"{scriptPath}\" {args}",
            WorkingDirectory       = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Read stdout and stderr concurrently to avoid deadlock on full pipe buffers.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout))
            Logger.Info($"[{scriptName}] {stdout.Trim().Replace("\n", " | ")}");
        if (!string.IsNullOrWhiteSpace(stderr))
            Logger.Warn($"[{scriptName}] stderr: {stderr.Trim().Replace("\n", " | ")}");

        if (proc.ExitCode != 0)
            Logger.Error($"[{scriptName}] exited with code {proc.ExitCode}.");

        return proc.ExitCode == 0;
    }
}
