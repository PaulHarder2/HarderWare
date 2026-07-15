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
        CancellationToken ct = default,
        IDictionary<string, string>? env = null)
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
            FileName = pythonExe,
            Arguments = $"\"{scriptPath}\" {args}",
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // On Windows the service runs with a minimal environment and no conda activation,
        // so augment PATH with the conda environment directories so the interpreter can
        // locate its dependent DLLs. On Linux (containerized) the interpreter is a system
        // Python that resolves its own shared libraries and these conda subdirs don't exist,
        // so the inherited PATH is already correct — leave it untouched.
        if (OperatingSystem.IsWindows())
        {
            var condaEnvDir = Path.GetDirectoryName(pythonExe)!;
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            psi.Environment["PATH"] = string.Join(';',
                condaEnvDir,
                Path.Combine(condaEnvDir, "Library", "bin"),
                Path.Combine(condaEnvDir, "Scripts"),
                existingPath);
        }

        if (env is not null)
            foreach (var (key, value) in env)
                psi.Environment[key] = value;

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Use CancellationToken.None for pipe reads: they complete naturally when
        // the process exits or is killed, so passing ct would cancel the reads
        // before the pipe closes and leave output unread.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Service is stopping.  Kill the Python process so it does not
            // continue running as an orphan after the service is redeployed.
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

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