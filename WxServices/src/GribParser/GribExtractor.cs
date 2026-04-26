using System.Diagnostics;

using WxServices.Logging;

namespace GribParser;

/// <summary>
/// Represents one meteorological value extracted from a GRIB2 file at a specific
/// grid point and vertical level.
/// </summary>
/// <param name="Variable">GRIB2 variable abbreviation (e.g. <c>"TMP"</c>, <c>"UGRD"</c>).</param>
/// <param name="Level">Level descriptor from the GRIB2 inventory (e.g. <c>"2 m above ground"</c>).</param>
/// <param name="Lat">Grid point latitude in decimal degrees (-90 to +90).</param>
/// <param name="Lon">Grid point longitude in decimal degrees (-180 to +180).</param>
/// <param name="Value">Scalar value in the native GFS unit for this variable.</param>
public record GribValue(string Variable, string Level, float Lat, float Lon, float Value);

/// <summary>
/// Extracts meteorological values from a GRIB2 file for a geographic sub-region
/// by invoking the native Windows <c>wgrib2.exe</c> binary as a subprocess.
/// </summary>
/// <remarks>
/// <para>
/// Prior to WX-33, this class invoked <c>wgrib2</c> via <c>wsl.exe</c>, which
/// required a per-user WSL distro and broke when the parent service moved to an
/// <c>NT SERVICE\*</c> virtual account (virtual accounts have no WSL of their
/// own).  The native NOAA Windows build of wgrib2 (Cygwin-compiled) runs under
/// any identity, so WX-33 dropped the WSL wrapper entirely.  Windows paths are
/// now passed to wgrib2.exe unchanged.
/// </para>
/// </remarks>
public static class GribExtractor
{
    /// <summary>
    /// Extracts all GRIB2 messages in <paramref name="grib2FilePath"/> that fall
    /// within the supplied bounding box, returning one <see cref="GribValue"/> per
    /// (variable x grid point) combination.
    /// </summary>
    /// <remarks>
    /// Internally runs two wgrib2 invocations:
    /// <list type="number">
    ///   <item><c>wgrib2 ... -small_grib ...</c> - crops the input to the bounding box,
    ///   producing a smaller temporary GRIB2 file.</item>
    ///   <item><c>wgrib2 ... -csv ...</c> - emits the sub-grid as CSV to a file.</item>
    /// </list>
    /// </remarks>
    /// <param name="grib2FilePath">Absolute Windows path to the input GRIB2 file.</param>
    /// <param name="wgrib2Path">Absolute Windows path to <c>wgrib2.exe</c> (e.g. <c>C:\HarderWare\wgrib2\wgrib2.exe</c>).</param>
    /// <param name="latMin">Southern boundary of the bounding box in decimal degrees.</param>
    /// <param name="latMax">Northern boundary of the bounding box in decimal degrees.</param>
    /// <param name="lonMin">Western boundary in decimal degrees (-180 to +180 convention).</param>
    /// <param name="lonMax">Eastern boundary in decimal degrees (-180 to +180 convention).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A list of extracted grid values; empty if the file produces no data within
    /// the bounding box.
    /// </returns>
    /// <sideeffects>
    /// Creates and then deletes a temporary <c>.sub.grb2</c> file and a <c>.csv</c>
    /// file alongside the input.  Spawns two <c>wgrib2.exe</c> subprocesses.
    /// Writes warning log entries if wgrib2 exits with a non-zero code or emits
    /// stderr output.
    /// </sideeffects>
    public static async Task<IReadOnlyList<GribValue>> ExtractAsync(
        string grib2FilePath,
        string wgrib2Path,
        float latMin, float latMax,
        float lonMin, float lonMax,
        CancellationToken ct = default)
    {
        // GFS uses 0-360 longitude convention; convert our -180/+180 bounds.
        var lon360Min = lonMin < 0 ? lonMin + 360f : lonMin;
        var lon360Max = lonMax < 0 ? lonMax + 360f : lonMax;

        var subgridPath = grib2FilePath + ".sub.grb2";
        var csvPath = grib2FilePath + ".csv";

        try
        {
            // Step 1: crop to bounding box.
            var step1Args = $"\"{grib2FilePath}\""
                          + $" -small_grib {lon360Min:F3}:{lon360Max:F3} {latMin:F3}:{latMax:F3}"
                          + $" \"{subgridPath}\"";

            var rc1 = await RunWgrib2Async(wgrib2Path, step1Args, null, ct);
            if (rc1 != 0)
                Logger.Warn($"GribExtractor: wgrib2 -small_grib exited {rc1} for '{Path.GetFileName(grib2FilePath)}'.");

            if (!File.Exists(subgridPath))
            {
                Logger.Warn($"GribExtractor: subgrid file not produced from '{Path.GetFileName(grib2FilePath)}' - bounding box may yield no points.");
                return Array.Empty<GribValue>();
            }

            // Step 2: emit CSV from the sub-grid.
            // wgrib2 opens its CSV output with fopen(), so we write to a file
            // rather than attempting to redirect through stdout.
            var step2Args = $"\"{subgridPath}\" -csv \"{csvPath}\"";

            var rc2 = await RunWgrib2Async(wgrib2Path, step2Args, null, ct);
            if (rc2 != 0)
                Logger.Warn($"GribExtractor: wgrib2 -csv exited {rc2} for sub-grid of '{Path.GetFileName(grib2FilePath)}'.");

            if (!File.Exists(csvPath))
                return Array.Empty<GribValue>();

            var csvLines = await File.ReadAllLinesAsync(csvPath, ct);
            return ParseCsv(csvLines);
        }
        finally
        {
            try { if (File.Exists(subgridPath)) File.Delete(subgridPath); }
            catch { /* best-effort cleanup */ }
            try { if (File.Exists(csvPath)) File.Delete(csvPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>wgrib2.exe</c> with the supplied arguments, optionally collecting
    /// every stdout line into <paramref name="outputLines"/>.
    /// </summary>
    /// <param name="wgrib2Path">Absolute Windows path to <c>wgrib2.exe</c>.</param>
    /// <param name="args">Arguments forwarded verbatim to <c>wgrib2.exe</c>.</param>
    /// <param name="outputLines">
    /// If non-null, each stdout line is appended here as it is read.
    /// If null, stdout is drained and discarded.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The process exit code.</returns>
    /// <sideeffects>
    /// Spawns a <c>wgrib2.exe</c> child process.
    /// Any stderr output is written to the warning log.
    /// </sideeffects>
    private static async Task<int> RunWgrib2Async(
        string wgrib2Path,
        string args,
        List<string>? outputLines,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(wgrib2Path, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start wgrib2.exe at '{wgrib2Path}' - is the path correct and does the service account have RX on that folder?");

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        if (outputLines is not null)
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
                outputLines.Add(line);
        }
        else
        {
            await proc.StandardOutput.ReadToEndAsync(ct);
        }

        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stderr))
            Logger.Warn($"wgrib2 stderr: {stderr.Trim()}");

        return proc.ExitCode;
    }

    /// <summary>
    /// Parses wgrib2 <c>-csv</c> output lines into <see cref="GribValue"/> records.
    /// </summary>
    /// <remarks>
    /// wgrib2 CSV output format (no header row).  Older versions emit 6 fields;
    /// newer versions (including 3.x) add a forecast-time column between level
    /// and lon, producing 7 fields:
    /// <code>
    /// 6-field: "YYYYMMDDCC","VARNAME","level",lon,lat,value
    /// 7-field: "YYYYMMDDCC","VARNAME","level","forecast",lon,lat,value
    /// </code>
    /// The parser reads variable and level from fixed positions and takes lon,
    /// lat, and value from the last three fields, so it handles both layouts.
    /// Longitudes are in the GFS 0-360 convention and are converted to -180/+180 here.
    /// Lines that cannot be parsed are silently skipped.
    /// </remarks>
    /// <param name="lines">Lines from a <c>wgrib2 -csv</c> output file.</param>
    /// <returns>All successfully parsed values.</returns>
    private static IReadOnlyList<GribValue> ParseCsv(IEnumerable<string> lines)
    {
        var results = new List<GribValue>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Minimum 6 fields; lon/lat/value are always the last three.
            var parts = line.Split(',');
            if (parts.Length < 6) continue;

            // Standard wgrib2 CSV: "refdate","var","level"[,"forecast"],lon,lat,value
            // Some builds prepend a second valid-time field (ISO datetime), shifting
            // variable and level one position to the right.  Detect by checking
            // whether parts[1] looks like a datetime (contains '-' or ':').
            var varOffset = parts[1].Contains('-') || parts[1].Contains(':') ? 1 : 0;
            var variable = parts[1 + varOffset].Trim('"', ' ');
            var level = parts[2 + varOffset].Trim('"', ' ');

            var lonIdx = parts.Length - 3;
            if (!float.TryParse(parts[lonIdx].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lon360)) continue;
            if (!float.TryParse(parts[lonIdx + 1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat)) continue;
            if (!float.TryParse(parts[lonIdx + 2].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)) continue;

            // Convert 0-360 longitude back to -180/+180.
            var lon = lon360 > 180f ? lon360 - 360f : lon360;

            results.Add(new GribValue(variable, level, lat, lon, value));
        }

        return results;
    }
}