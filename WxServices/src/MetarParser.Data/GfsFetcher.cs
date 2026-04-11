using GribParser;
using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Downloads GFS model-forecast data from NOAA NOMADS for the configured bounding
/// box, extracts values via wgrib2, and inserts the results into the
/// <c>GfsGrid</c> database table.
/// </summary>
/// <remarks>
/// <para>
/// GFS runs four times per day at 00Z, 06Z, 12Z, and 18Z.  Files are posted
/// incrementally to NOMADS starting roughly 3.5–4 hours after model initialisation.
/// This class determines the most recent run that should be available, fetches all
/// forecast hours from 0 to <c>maxForecastHours</c> that are not yet stored, and
/// stops as soon as a forecast hour file is missing (indicating the run is still
/// being computed).
/// </para>
/// <para>
/// Eight variables are downloaded per forecast hour via byte-range HTTP requests
/// against the NOMADS pgrb2 0.25° files:
/// TMP (2 m temperature), SPFH (2 m specific humidity → dew point), UGRD / VGRD
/// (10 m wind components), PRATE (precipitation rate), TCDC (total cloud cover),
/// CAPE (surface convective energy), and PRMSL (mean sea-level pressure).
/// </para>
/// </remarks>
public static class GfsFetcher
{
    // AWS Open Data mirror — same files and .idx format as NOMADS, no rate limits.
    private const string NomadsBase =
        "https://noaa-gfs-bdp-pds.s3.amazonaws.com";

    /// <summary>
    /// Variable:level keys that must be matched in the NOMADS .idx inventory file.
    /// These correspond exactly to the GfsGridPoint entity fields.
    /// </summary>
    private static readonly HashSet<string> TargetVars = new(StringComparer.Ordinal)
    {
        "TMP:2 m above ground",
        "SPFH:2 m above ground",
        "UGRD:10 m above ground",
        "VGRD:10 m above ground",
        "PRATE:surface",
        "TCDC:entire atmosphere",
        "CAPE:surface",
        "PRMSL:mean sea level"
    };

    /// <summary>
    /// Fetches outstanding GFS forecast data for the most recent available model run
    /// and inserts any new grid points into the database.  Purges old runs afterwards.
    /// </summary>
    /// <param name="homeLat">Centre latitude of the bounding box in decimal degrees.</param>
    /// <param name="homeLon">Centre longitude of the bounding box in decimal degrees (−180/+180).</param>
    /// <param name="boxDegrees">Half-width of the bounding box in degrees (applied in all four directions).</param>
    /// <param name="dbOptions">EF Core options for opening <see cref="WeatherDataContext"/> instances.</param>
    /// <param name="httpClient">HTTP client for NOMADS requests.</param>
    /// <param name="wgrib2WslPath">Absolute WSL path to the wgrib2 binary.</param>
    /// <param name="maxForecastHours">Highest forecast hour to download (inclusive). Default 120.</param>
    /// <param name="retainModelRuns">Number of most-recent model runs to keep in the database. Default 2.</param>
    /// <param name="gfsTempPath">
    /// Windows directory for temporary GRIB2, sub-grid, and CSV files.
    /// Created automatically if absent.  Defaults to <c>C:\HarderWare\temp</c>.
    /// </param>
    /// <param name="delayHours">
    /// Minimum hours after a model run's nominal time before the fetcher will
    /// attempt to download it.  Avoids 404s during the window before NOAA begins
    /// posting output.  Default 3.5.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <sideeffects>
    /// Makes HTTP requests to NOMADS.
    /// Creates and deletes temporary GRIB2 files in <paramref name="gfsTempPath"/>.
    /// Invokes wgrib2 via wsl.exe subprocesses.
    /// Inserts <see cref="GfsGridPoint"/> rows and deletes old rows in the database.
    /// Writes log entries throughout.
    /// </sideeffects>
    public static async Task FetchAndInsertAsync(
        WxServices.Common.FetchRegion region,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient,
        string wgrib2WslPath,
        string gfsTempPath,
        int maxForecastHours = 120,
        int retainModelRuns  = 2,
        double delayHours    = 3.5,
        CancellationToken ct = default)
    {
        // ── Determine which run to process ────────────────────────────────────
        // Prefer an existing incomplete run already registered in the database.
        // This allows a previous run to be manually re-queued by marking it
        // incomplete (or inserting a new record), without waiting for the next
        // computed cycle.  Falls back to the latest available run if nothing
        // is pending.
        DateTime modelRun;
        HashSet<int> storedHours;

        using (var ctx = new WeatherDataContext(dbOptions))
        {
            var pendingRun = await ctx.GfsModelRuns
                .Where(r => !r.IsComplete)
                .OrderByDescending(r => r.ModelRunUtc)
                .FirstOrDefaultAsync(ct);

            if (pendingRun is not null)
            {
                modelRun = pendingRun.ModelRunUtc;
                Logger.Info($"GfsFetcher: resuming incomplete run {modelRun:yyyy-MM-dd HH}Z.");
            }
            else
            {
                modelRun = LatestAvailableModelRun(delayHours);
                Logger.Info($"GfsFetcher: latest available model run is {modelRun:yyyy-MM-dd HH}Z.");

                var runRecord = await ctx.GfsModelRuns
                    .FirstOrDefaultAsync(r => r.ModelRunUtc == modelRun, ct);

                if (runRecord?.IsComplete == true)
                {
                    Logger.Info($"GfsFetcher: run {modelRun:yyyy-MM-dd HH}Z is already marked complete — skipping.");
                    return;
                }

                if (runRecord is null)
                {
                    ctx.GfsModelRuns.Add(new GfsModelRun { ModelRunUtc = modelRun, IsComplete = false });
                    await ctx.SaveChangesAsync(ct);
                    Logger.Info($"GfsFetcher: registered new run {modelRun:yyyy-MM-dd HH}Z.");
                }
            }

            // Find which hours are already stored (supports resuming after a restart).
            storedHours = (await ctx.GfsGrid
                .Where(g => g.ModelRunUtc == modelRun)
                .Select(g => g.ForecastHour)
                .Distinct()
                .ToListAsync(ct))
                .ToHashSet();
            Logger.Info($"GfsFetcher: {storedHours.Count} hour(s) already stored for run {modelRun:yyyy-MM-dd HH}Z.");
        }

        var runDate  = modelRun.ToString("yyyyMMdd");
        var runCycle = modelRun.Hour.ToString("D2");

        var latMin = (float)region.South;
        var latMax = (float)region.North;
        var lonMin = (float)region.West;
        var lonMax = (float)region.East;

        // ── Ensure temp directory exists and clean up any stale files ────────
        Directory.CreateDirectory(gfsTempPath);
        CleanupTempFiles(gfsTempPath);

        int totalInserted = 0;

        // ── Fetch each forecast hour ──────────────────────────────────────────
        for (int fh = 0; fh <= maxForecastHours; fh++)
        {
            if (ct.IsCancellationRequested) break;

            if (fh % 10 == 0)
                Logger.Info($"GfsFetcher: f{fh:D3}/{maxForecastHours} — {totalInserted} records inserted this cycle.");

            if (storedHours.Contains(fh)) continue;

            var fhStr   = fh.ToString("D3");
            var baseUrl = $"{NomadsBase}/gfs.{runDate}/{runCycle}/atmos/gfs.t{runCycle}z.pgrb2.0p25.f{fhStr}";

            // ── Download inventory (.idx) ─────────────────────────────────────
            string idxContent;
            try
            {
                idxContent = await httpClient.GetStringAsync(baseUrl + ".idx", ct);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.NotFound ||
                ex.StatusCode == System.Net.HttpStatusCode.Redirect ||
                ex.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
            {
                Logger.Info($"GfsFetcher: f{fhStr} not yet on NOMADS ({(int?)ex.StatusCode}) — stopping this cycle.");
                break; // Files appear in hour order; later hours won't be there either.
            }
            catch (Exception ex)
            {
                Logger.Error($"GfsFetcher: failed to fetch index for f{fhStr}: {ex.Message}");
                break;
            }

            // ── Parse byte ranges from inventory ─────────────────────────────
            var ranges = ParseIndex(idxContent);
            if (ranges.Count < TargetVars.Count)
            {
                var missing = TargetVars.Except(ranges.Keys).ToList();
                Logger.Warn($"GfsFetcher: f{fhStr} index missing {missing.Count} variable(s): " +
                            $"{string.Join(", ", missing)} — skipping.");
                continue;
            }

            // ── Download variable byte-ranges into a single temp GRIB2 file ──
            var tempPath = Path.Combine(
                gfsTempPath, $"gfs_{runDate}_{runCycle}_f{fhStr}.grb2");

            try
            {
                var downloaded = await DownloadVariablesAsync(baseUrl, ranges, tempPath, httpClient, ct);
                if (downloaded < TargetVars.Count)
                {
                    Logger.Warn($"GfsFetcher: f{fhStr} only {downloaded}/{TargetVars.Count} variables downloaded — skipping.");
                    continue;
                }

                // ── Extract sub-grid values via wgrib2 ────────────────────────
                var gribValues = await GribExtractor.ExtractAsync(
                    tempPath, wgrib2WslPath, latMin, latMax, lonMin, lonMax, ct);

                if (gribValues.Count == 0)
                {
                    Logger.Warn($"GfsFetcher: wgrib2 returned no values for f{fhStr}.");
                    continue;
                }

                // ── Assemble entities and insert ──────────────────────────────
                var points = AssembleGridPoints(gribValues, modelRun, fh);

                using var insertCtx = new WeatherDataContext(dbOptions);
                insertCtx.GfsGrid.AddRange(points);
                try
                {
                    await insertCtx.SaveChangesAsync(ct);
                    totalInserted += points.Count;
                    Logger.Debug($"GfsFetcher: f{fhStr} — {points.Count} records inserted.");
                }
                catch (DbUpdateException ex)
                {
                    Logger.Error($"GfsFetcher: DB error inserting f{fhStr}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch { /* best-effort cleanup */ }
            }

            // Brief pause between forecast hours to be a polite AWS S3 client.
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        Logger.Info($"GfsFetcher: run {modelRun:yyyy-MM-dd HH}Z fetch done — {totalInserted} records inserted.");

        // ── Mark run complete if every forecast hour 0..maxForecastHours is stored ──
        using (var ctx = new WeatherDataContext(dbOptions))
        {
            // Count distinct forecast hours in GfsGrid for this run.
            // Expected: maxForecastHours + 1 (hours 0 through maxForecastHours inclusive).
            var storedHourCount = await ctx.GfsGrid
                .Where(g => g.ModelRunUtc == modelRun)
                .Select(g => g.ForecastHour)
                .Distinct()
                .CountAsync(ct);

            var expectedHours = maxForecastHours + 1;

            if (storedHourCount >= expectedHours)
            {
                var runRecord = await ctx.GfsModelRuns
                    .FirstOrDefaultAsync(r => r.ModelRunUtc == modelRun, ct);

                if (runRecord is not null && !runRecord.IsComplete)
                {
                    runRecord.IsComplete = true;
                    await ctx.SaveChangesAsync(ct);
                    Logger.Info($"GfsFetcher: run {modelRun:yyyy-MM-dd HH}Z marked complete ({storedHourCount}/{expectedHours} hours stored).");
                }
            }
            else
            {
                Logger.Info($"GfsFetcher: run {modelRun:yyyy-MM-dd HH}Z is {storedHourCount}/{expectedHours} hours complete — will resume next cycle.");
            }
        }

        await PurgeOldRunsAsync(dbOptions, retainModelRuns, ct);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Deletes any GFS temporary files left in <paramref name="tempDir"/> by a
    /// previous fetch cycle that was interrupted before its finally blocks ran.
    /// </summary>
    /// <param name="tempDir">Directory to scan, as configured by <c>Gfs:TempPath</c>.</param>
    /// <sideeffects>Deletes files matching <c>gfs_*.grb2*</c> in <paramref name="tempDir"/>. Writes a log entry for each file removed.</sideeffects>
    private static void CleanupTempFiles(string tempDir)
    {
        foreach (var file in Directory.EnumerateFiles(tempDir, "gfs_*.grb2*"))
        {
            try
            {
                File.Delete(file);
                Logger.Debug($"GfsFetcher: removed stale temp file '{Path.GetFileName(file)}'.");
            }
            catch (Exception ex)
            {
                Logger.Warn($"GfsFetcher: could not delete stale temp file '{Path.GetFileName(file)}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Returns the UTC <see cref="DateTime"/> of the most recent GFS model run
    /// whose data is likely to have started appearing on NOMADS/AWS.
    /// </summary>
    /// <remarks>
    /// GFS runs at 00Z, 06Z, 12Z, and 18Z.  Files are posted incrementally
    /// starting roughly 4–5 hours after model initialisation.  This method
    /// skips any run younger than <paramref name="delayHours"/> to avoid
    /// pointless 404 requests, while still accepting runs up to 8 hours old.
    /// </remarks>
    /// <param name="delayHours">
    /// Minimum age (in hours) a run must have before it is considered.
    /// </param>
    /// <returns>The run time of the most recent eligible model run.</returns>
    private static DateTime LatestAvailableModelRun(double delayHours = 3.5)
    {
        var now = DateTime.UtcNow;

        foreach (var cycle in new[] { 18, 12, 6, 0 })
        {
            var runTime  = now.Date.AddHours(cycle);
            var ageHours = (now - runTime).TotalHours;
            if (ageHours >= delayHours && ageHours <= 8)
                return runTime;
        }

        // Check yesterday's 18Z if nothing from today qualifies yet.
        var yesterday18Z = now.Date.AddDays(-1).AddHours(18);
        var age18Z = (now - yesterday18Z).TotalHours;
        if (age18Z >= delayHours && age18Z <= 14)
            return yesterday18Z;

        // Safety fallback.
        return now.Date.AddDays(-1).AddHours(18);
    }

    /// <summary>
    /// Parses a NOMADS GRIB2 inventory (.idx) file and returns the byte range
    /// for each of the <see cref="TargetVars"/>.
    /// </summary>
    /// <remarks>
    /// The .idx line format is:
    /// <code>lineNum:byteOffset:d=YYYYMMDDCC:variable:level:temporal:</code>
    /// The byte range for a record spans from its offset to one byte before the
    /// next record's offset (or open-ended for the last record in the file).
    /// </remarks>
    /// <param name="idxContent">Full text content of the .idx file.</param>
    /// <returns>
    /// A dictionary keyed by variable:level (e.g. <c>"TMP:2 m above ground"</c>)
    /// with the inclusive byte start and nullable end (null = read to EOF).
    /// </returns>
    private static Dictionary<string, (long Start, long? End)> ParseIndex(string idxContent)
    {
        // Build a flat list of all (varLevel, byteOffset) from every .idx line.
        var all = new List<(string VarLevel, long Offset)>();

        foreach (var line in idxContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':');
            if (parts.Length < 5) continue;
            if (!long.TryParse(parts[1], out var offset)) continue;
            all.Add(($"{parts[3]}:{parts[4]}", offset));
        }

        var result = new Dictionary<string, (long, long?)>(StringComparer.Ordinal);

        for (int i = 0; i < all.Count; i++)
        {
            if (!TargetVars.Contains(all[i].VarLevel)) continue;

            long? end = (i + 1 < all.Count) ? all[i + 1].Offset - 1 : null;
            result[all[i].VarLevel] = (all[i].Offset, end);
        }

        return result;
    }

    /// <summary>
    /// Downloads byte ranges for each target variable from the NOMADS GRIB2 file
    /// and writes the concatenated data to <paramref name="destPath"/>.
    /// </summary>
    /// <remarks>
    /// Each byte-range download produces a complete GRIB2 message for one variable.
    /// GRIB2 messages concatenate to a valid multi-message GRIB2 file, which wgrib2
    /// can process directly.
    /// </remarks>
    /// <param name="dataUrl">Base URL of the GRIB2 data file (without <c>.idx</c>).</param>
    /// <param name="ranges">Byte ranges keyed by variable:level, as returned by <see cref="ParseIndex"/>.</param>
    /// <param name="destPath">Windows path to write the concatenated GRIB2 data to.</param>
    /// <param name="httpClient">HTTP client for the range requests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of variables successfully downloaded and written to <paramref name="destPath"/>.
    /// A value less than the total number of target variables indicates that one or more
    /// byte-range requests failed; the caller should skip the forecast hour in that case.
    /// </returns>
    /// <sideeffects>Creates or overwrites the file at <paramref name="destPath"/>.</sideeffects>
    private static async Task<int> DownloadVariablesAsync(
        string dataUrl,
        Dictionary<string, (long Start, long? End)> ranges,
        string destPath,
        HttpClient httpClient,
        CancellationToken ct)
    {
        await using var output = File.Create(destPath);
        int succeeded = 0;

        foreach (var (varLevel, (start, end)) in ranges)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, dataUrl);
            request.Headers.Range =
                new System.Net.Http.Headers.RangeHeaderValue(start, end);

            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"GfsFetcher: byte-range request for '{varLevel}' returned {(int)response.StatusCode}.");
                continue;
            }

            await using var content = await response.Content.ReadAsStreamAsync(ct);
            await content.CopyToAsync(output, ct);
            succeeded++;
        }

        return succeeded;
    }

    /// <summary>
    /// Converts a flat list of <see cref="GribValue"/> records into
    /// <see cref="GfsGridPoint"/> entities, one per unique (lat, lon) coordinate.
    /// </summary>
    /// <remarks>
    /// Unit conversions applied during assembly:
    /// <list type="bullet">
    ///   <item>TMP: Kelvin → Celsius (subtract 273.15)</item>
    ///   <item>SPFH: specific humidity (kg/kg) → dew-point Celsius via the Magnus formula
    ///   using standard sea-level pressure (1013.25 hPa).</item>
    ///   <item>All other fields: no conversion; native GFS units are retained.</item>
    /// </list>
    /// </remarks>
    /// <param name="values">Extracted GRIB values for a single forecast hour.</param>
    /// <param name="modelRunUtc">Model initialisation time (UTC).</param>
    /// <param name="forecastHour">Forecast hour offset from <paramref name="modelRunUtc"/>.</param>
    /// <returns>One <see cref="GfsGridPoint"/> per unique grid coordinate.</returns>
    private static List<GfsGridPoint> AssembleGridPoints(
        IReadOnlyList<GribValue> values,
        DateTime modelRunUtc,
        int forecastHour)
    {
        var points = new List<GfsGridPoint>();

        foreach (var group in values.GroupBy(v => (v.Lat, v.Lon)))
        {
            var byKey = group.ToDictionary(
                v => $"{v.Variable}:{v.Level}", StringComparer.Ordinal);

            var point = new GfsGridPoint
            {
                ModelRunUtc  = modelRunUtc,
                ForecastHour = forecastHour,
                Lat          = group.Key.Lat,
                Lon          = group.Key.Lon,
            };

            if (byKey.TryGetValue("TMP:2 m above ground",    out var tmp))
                point.TmpC = tmp.Value - 273.15f;

            if (byKey.TryGetValue("SPFH:2 m above ground",   out var spfh))
                point.DwpC = SpfhToDewPointC(spfh.Value);

            if (byKey.TryGetValue("UGRD:10 m above ground",  out var ugrd))
                point.UGrdMs = ugrd.Value;

            if (byKey.TryGetValue("VGRD:10 m above ground",  out var vgrd))
                point.VGrdMs = vgrd.Value;

            if (byKey.TryGetValue("PRATE:surface",           out var prate))
                point.PRateKgM2s = prate.Value;

            if (byKey.TryGetValue("TCDC:entire atmosphere",  out var tcdc))
                point.TcdcPct = tcdc.Value;

            if (byKey.TryGetValue("CAPE:surface",            out var cape))
                point.CapeJKg = cape.Value;

            if (byKey.TryGetValue("PRMSL:mean sea level",   out var prmsl))
                point.PrMslPa = prmsl.Value;

            points.Add(point);
        }

        return points;
    }

    /// <summary>
    /// Converts 2-metre specific humidity to dew-point temperature using the
    /// Magnus formula, assuming standard sea-level pressure.
    /// </summary>
    /// <param name="q">Specific humidity in kg kg⁻¹.</param>
    /// <param name="pressureHpa">
    /// Ambient pressure in hPa used to compute vapour pressure.
    /// Defaults to 1013.25 hPa (standard sea-level pressure).
    /// </param>
    /// <returns>Dew-point temperature in degrees Celsius.</returns>
    private static float SpfhToDewPointC(float q, float pressureHpa = 1013.25f)
    {
        // Vapour pressure from mixing ratio: e = q*P / (0.622 + 0.378*q)
        var e = q * pressureHpa / (0.622f + 0.378f * q);
        if (e <= 0f) return float.NaN;

        // Magnus formula: Td = (243.04 * ln(e/6.112)) / (17.67 - ln(e/6.112))
        var logE = MathF.Log(e / 6.112f);
        return 243.04f * logE / (17.67f - logE);
    }

    /// <summary>
    /// Deletes GFS data for model runs older than the
    /// <paramref name="retainCount"/> most recent runs, from both
    /// <c>GfsGrid</c> and <c>GfsModelRuns</c>.
    /// </summary>
    /// <remarks>
    /// The retention count applies to all tracked runs regardless of completion
    /// status, so an in-progress run counts toward the total.  With the default
    /// of 2, the previous complete run is retained alongside the in-progress run;
    /// only once a third run appears does the oldest get deleted.
    /// </remarks>
    /// <param name="dbOptions">EF Core options.</param>
    /// <param name="retainCount">Number of most-recent model runs to keep.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <sideeffects>Deletes rows from <c>GfsGrid</c> and <c>GfsModelRuns</c>. Writes a log entry if any runs are purged.</sideeffects>
    private static async Task PurgeOldRunsAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        int retainCount,
        CancellationToken ct)
    {
        using var ctx = new WeatherDataContext(dbOptions);
        ctx.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));

        // Keep at least retainCount runs, but never delete a run that is newer
        // than the latest complete run — an in-progress run must survive even
        // if it pushes the total count above retainCount.
        var latestComplete = await ctx.GfsModelRuns
            .Where(r => r.IsComplete)
            .OrderByDescending(r => r.ModelRunUtc)
            .Select(r => (DateTime?)r.ModelRunUtc)
            .FirstOrDefaultAsync(ct);

        var allRuns = await ctx.GfsModelRuns
            .OrderByDescending(r => r.ModelRunUtc)
            .Select(r => r.ModelRunUtc)
            .ToListAsync(ct);

        if (allRuns.Count <= retainCount) return;

        // Only delete runs older than the latest complete run.
        var runsToDelete = allRuns
            .Skip(retainCount)
            .Where(r => latestComplete.HasValue && r < latestComplete.Value)
            .ToList();

        foreach (var run in runsToDelete)
        {
            var deleted = await ctx.GfsGrid
                .Where(g => g.ModelRunUtc == run)
                .ExecuteDeleteAsync(ct);

            await ctx.GfsModelRuns
                .Where(r => r.ModelRunUtc == run)
                .ExecuteDeleteAsync(ct);

            Logger.Info($"GfsFetcher: purged model run {run:yyyy-MM-dd HH}Z ({deleted:N0} grid rows deleted).");
        }
    }
}
