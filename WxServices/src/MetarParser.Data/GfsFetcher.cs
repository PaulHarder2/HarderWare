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
    /// <param name="wgrib2Path">Absolute Windows path to wgrib2.exe.</param>
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
    /// Invokes wgrib2.exe subprocesses directly (no WSL wrapper since WX-33).
    /// Inserts <see cref="GfsGridPoint"/> rows and deletes old rows in the database.
    /// Writes log entries throughout.
    /// </sideeffects>
    public static async Task FetchAndInsertAsync(
        WxServices.Common.FetchRegion region,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient,
        string wgrib2Path,
        string gfsTempPath,
        int maxForecastHours = 120,
        int retainModelRuns = 2,
        double delayHours = 3.5,
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

        var runDate = modelRun.ToString("yyyyMMdd");
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

            var fhStr = fh.ToString("D3");
            var baseUrl = $"{NomadsBase}/gfs.{runDate}/{runCycle}/atmos/gfs.t{runCycle}z.pgrb2.0p25.f{fhStr}";

            // ── Download inventory (.idx) ─────────────────────────────────────
            string idxContent;
            try
            {
                idxContent = await httpClient.GetStringWithRetryAsync(
                    baseUrl + ".idx", $"GFS f{fhStr} index", ct: ct);
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
                Logger.Error($"GfsFetcher: failed to fetch index for f{fhStr} after retries: {ex.Message}");
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
                    tempPath, wgrib2Path, latMin, latMax, lonMin, lonMax, ct);

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

        // ── Mark run complete only if EVERY forecast hour 0..maxForecastHours is stored ──
        await EvaluateRunCompletenessAsync(dbOptions, modelRun, maxForecastHours, ct);

        await PurgeOldRunsAsync(dbOptions, retainModelRuns, ct);
    }

    // ── private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Set once the invalid-<c>MaxForecastHours</c> misconfiguration has been reported, so it is
    /// logged once per ONSET rather than on every fetch cycle.  Cleared on the valid path, so a
    /// second onset after the setting is corrected is reported again.  See the guard in
    /// <see cref="EvaluateRunCompletenessAsync"/> for why the rate limit exists (WX-453).
    /// </summary>
    /// <remarks>
    /// An <see cref="int"/> rather than a <see cref="bool"/> so the test-and-set is a single
    /// <see cref="Interlocked.Exchange(ref int, int)"/> and no read-modify-write can be torn.
    /// <para>
    /// ⚠️ NARROWED. This claimed correctness "even if two fetch cycles ever overlap", which held
    /// while the flag was a one-way LATCH and stopped holding when the valid path began clearing
    /// it: set and clear are now two independent operations, so overlapping cycles could report one
    /// onset twice or suppress a later one. The once-per-onset property therefore rests on
    /// <c>GfsFetchWorker.ExecuteAsync</c>'s single sequential cycle loop, not on the interlock.
    /// Correct as deployed; do not rely on the wider guarantee. (CodeRabbit, PR #227.)
    /// </para>
    /// </remarks>
    private static int _negativeBoundReported;

    /// <summary>
    /// Decides whether <paramref name="modelRun"/> is completely stored, marking it
    /// <see cref="GfsModelRun.IsComplete"/> when it is and logging <em>which</em> hours are
    /// absent when it is not.
    /// </summary>
    /// <remarks>
    /// Completeness is <b>set membership</b> over <c>0..maxForecastHours</c>, never a count of
    /// distinct hours.  A count is not a completeness test: the previous
    /// <c>storedHourCount &gt;= expectedHours</c> meant any hour stored <em>outside</em> the
    /// expected range substituted one-for-one for a missing hour inside it, so a run could be
    /// marked complete with hours genuinely absent (WX-451).
    /// <para>
    /// The stored set is re-read here rather than carried from the pre-fetch resume scan, which
    /// is stale by this point — and because the database is the artifact, while our own
    /// bookkeeping is only a marker.
    /// </para>
    /// </remarks>
    /// <param name="dbOptions">EF Core options.</param>
    /// <param name="modelRun">The run to evaluate.</param>
    /// <param name="maxForecastHours">Highest expected forecast hour, inclusive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The missing forecast hours — empty when the run is complete — or <see langword="null"/>
    /// when <paramref name="maxForecastHours"/> is negative and completeness cannot be evaluated.
    /// </returns>
    /// <sideeffects>May set <c>IsComplete</c> on the run's <c>GfsModelRuns</c> row.</sideeffects>
    internal static async Task<IReadOnlyList<int>?> EvaluateRunCompletenessAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        DateTime modelRun,
        int maxForecastHours,
        CancellationToken ct)
    {
        if (maxForecastHours < 0)
        {
            // ERROR rather than WARN, deliberately: WxMonitor's LogScanner alerts at ERROR and
            // above and its email body carries the matched line, so this misconfiguration reaches
            // an operator instead of sitting in a log nobody reads.  It must be corrected — while
            // it holds, no run can ever be marked complete.
            //
            // Logged ONCE PER PROCESS, not once per cycle.  The guard's BEHAVIOUR is unaffected —
            // it refuses on every call — but the log line is rate-limited on purpose: WxMonitor
            // coalesces every ERROR from a service into a single finding whose cooldown DISCARDS
            // what it suppresses rather than deferring it (WX-453), so an ERROR repeating every
            // cycle holds that cooldown open indefinitely and destroys every other WxParser.Svc
            // error landing in the gaps.  This is a configuration fault — constant for the life of
            // the process — so repeats carry no new information, and WxMonitor already caps
            // delivery at one email per service per cooldown window regardless.
            if (Interlocked.Exchange(ref _negativeBoundReported, 1) == 0)
            {
                Logger.Error(
                    $"GfsFetcher: Gfs:MaxForecastHours is {maxForecastHours}, which is invalid. " +
                    $"Completeness cannot be evaluated for run {modelRun:yyyy-MM-dd HH}Z, and no run " +
                    "will be marked complete until this setting is corrected. This is reported once " +
                    "per process; the condition is re-checked, and still refused, on every cycle.");
            }

            return null;
        }

        // RESET ON THE VALID PATH, so a SECOND onset is reported (WX-451 review round 2, finding 4).
        // The flag is a rate limit on a STANDING condition, not a once-per-process latch: the bound
        // genuinely changes within one process, because GfsFetchWorker.ExecuteAsync calls LoadConfig()
        // inside its cycle loop and IConfiguration is built with reloadOnChange:true over the JSON
        // layers plus the WX-313 DB Config overlay.  Without this line the sequence
        //   bad -> ERROR (correct) -> operator corrects it -> bad again
        // logs NOTHING the second time, while no run can be marked complete, WxMonitor raises nothing
        // and WX-451-verify.sh's badconfig row reads 0 [ok] — the alarm for the exact silent freeze
        // this ticket exists to prevent, suppressed by its own rate limiter.
        //
        // Preserves the WX-453 rationale intact: still ONE ERROR PER ONSET rather than one per cycle,
        // so a standing misconfiguration cannot hold WxMonitor's cooldown open and discard every other
        // WxParser.Svc error.
        Interlocked.Exchange(ref _negativeBoundReported, 0);

        using var ctx = new WeatherDataContext(dbOptions);

        var storedHours = (await ctx.GfsGrid
            .Where(g => g.ModelRunUtc == modelRun)
            .Select(g => g.ForecastHour)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        var missingHours = ComputeMissingHours(storedHours, maxForecastHours);
        var expectedHours = maxForecastHours + 1;

        // Both lines below report IN-RANGE coverage, which can never exceed expectedHours — so
        // without this the fact that out-of-range hours exist could not appear in the log at all.
        // That matters precisely under a horizon REDUCTION, which is one of the two conditions
        // that make WX-451's defect reachable: the run would report a tidy "61/61 hours stored"
        // while silently holding 60 orphaned hours from the larger bound. Empty in normal
        // operation, so it costs nothing until something has actually changed underneath us.
        var outOfRange = CountOutOfRangeHours(storedHours, maxForecastHours);
        var outOfRangeNote = FormatOutOfRangeNote(outOfRange, maxForecastHours);

        if (missingHours.Count == 0)
        {
            var runRecord = await ctx.GfsModelRuns
                .FirstOrDefaultAsync(r => r.ModelRunUtc == modelRun, ct);

            if (runRecord is not null && !runRecord.IsComplete)
            {
                runRecord.IsComplete = true;
                await ctx.SaveChangesAsync(ct);
                Logger.Info(FormatCompleteLog(modelRun, expectedHours, outOfRangeNote));
            }
        }
        else
        {
            Logger.Info(FormatIncompleteLog(modelRun, expectedHours, missingHours, outOfRangeNote));
        }

        return missingHours;
    }

    /// <summary>
    /// Returns the forecast hours in <c>0..maxForecastHours</c> absent from
    /// <paramref name="storedHours"/>, ascending.  Pure; the caller guards a negative bound.
    /// </summary>
    /// <remarks>
    /// Hours present in <paramref name="storedHours"/> but outside the expected range are
    /// <em>ignored</em> rather than counted — which is the whole point of WX-451.
    /// </remarks>
    /// <param name="storedHours">Distinct forecast hours currently stored for the run.</param>
    /// <param name="maxForecastHours">Highest expected forecast hour, inclusive.</param>
    /// <returns>The absent hours; empty when every expected hour is present.</returns>
    internal static IReadOnlyList<int> ComputeMissingHours(ISet<int> storedHours, int maxForecastHours)
        => Enumerable
            .Range(0, maxForecastHours + 1)
            .Where(fh => !storedHours.Contains(fh))
            .ToList();

    /// <summary>
    /// Counts stored hours lying <em>outside</em> <c>0..maxForecastHours</c> — surplus rather
    /// than missing. Pure.
    /// </summary>
    /// <remarks>
    /// Extracted so the arithmetic behind the log's out-of-range note is pinned by a test. It
    /// previously sat inline, where the only test touching that scenario asserted just the
    /// completeness verdict — so deleting the note entirely left the suite green, and the sole
    /// production signal for a horizon REDUCTION (one of the two routes that make WX-451's
    /// defect reachable) was exercised by nothing.
    /// <para>
    /// ⚠️ Extracting the arithmetic closed only half of that. Until the log <em>text</em> was also
    /// extracted (<see cref="FormatOutOfRangeNote"/> and the two Format*Log helpers below) deleting
    /// the note from the message still left the suite green, because nothing asserted what the
    /// message said — an earlier revision of this remark claimed the coverage the extraction had
    /// not yet achieved (review round 2, finding 3).
    /// </para>
    /// </remarks>
    /// <param name="storedHours">Distinct forecast hours currently stored for the run.</param>
    /// <param name="maxForecastHours">Highest expected forecast hour, inclusive.</param>
    /// <returns>How many stored hours fall outside the expected range; 0 in normal operation.</returns>
    internal static int CountOutOfRangeHours(ISet<int> storedHours, int maxForecastHours)
        => storedHours.Count(h => h < 0 || h > maxForecastHours);

    /// <summary>
    /// Renders the surplus-hours suffix appended to both completeness log lines, or the empty
    /// string when nothing is out of range. Pure.
    /// </summary>
    /// <param name="outOfRange">Count from <see cref="CountOutOfRangeHours"/>.</param>
    /// <param name="maxForecastHours">Highest expected forecast hour, inclusive.</param>
    /// <returns>A leading-semicolon clause, or <see cref="string.Empty"/>.</returns>
    internal static string FormatOutOfRangeNote(int outOfRange, int maxForecastHours)
        => outOfRange > 0
            ? $"; {outOfRange} stored hour(s) outside 0..{maxForecastHours}"
            : string.Empty;

    /// <summary>
    /// Renders the run-marked-complete log line. Pure.
    /// </summary>
    /// <param name="modelRun">The run being reported.</param>
    /// <param name="expectedHours">Count of expected hours, i.e. <c>maxForecastHours + 1</c>.</param>
    /// <param name="outOfRangeNote">Suffix from <see cref="FormatOutOfRangeNote"/>.</param>
    /// <returns>The message passed to the logger.</returns>
    internal static string FormatCompleteLog(DateTime modelRun, int expectedHours, string outOfRangeNote)
        => $"GfsFetcher: run {modelRun:yyyy-MM-dd HH}Z marked complete " +
           $"({expectedHours}/{expectedHours} hours stored{outOfRangeNote}).";

    /// <summary>
    /// Renders the run-incomplete log line — the one carrying <em>which</em> hours are absent. Pure.
    /// </summary>
    /// <remarks>
    /// 🔴 THIS STRING IS THE FUNCTIONAL TEST'S ONLY DISCRIMINATOR, which is why it is extracted and
    /// asserted rather than left inline in the logger call.  <c>docs/test-procedures/WX-451-verify.sh</c>
    /// greps for the literal <c>"hours complete — missing "</c> to tell a 1.61.3+ binary from the old
    /// one: the replaced code emitted no missing-hours list at all, so this phrase cannot come from it.
    /// The complete-branch line is NOT usable for that — it is byte-identical across both versions for
    /// a healthy run.
    /// <para>
    /// So a refactor that drops, reorders or re-punctuates this phrase would silently turn the
    /// functional test into a permanent WAIT that can never PASS, with nothing going red. Before this
    /// extraction the 22 unit tests contained zero log assertions and all stayed green with the
    /// missing-hours clause deleted entirely (review round 2, finding 2).  The em-dash is U+2014 and
    /// the verify script matches it byte-for-byte; changing it to a hyphen breaks the test.
    /// </para>
    /// </remarks>
    /// <param name="modelRun">The run being reported.</param>
    /// <param name="expectedHours">Count of expected hours, i.e. <c>maxForecastHours + 1</c>.</param>
    /// <param name="missingHours">The absent hours; must be non-empty for this branch.</param>
    /// <param name="outOfRangeNote">Suffix from <see cref="FormatOutOfRangeNote"/>.</param>
    /// <returns>The message passed to the logger.</returns>
    internal static string FormatIncompleteLog(
        DateTime modelRun,
        int expectedHours,
        IReadOnlyList<int> missingHours,
        string outOfRangeNote)
        => $"GfsFetcher: run {modelRun:yyyy-MM-dd HH}Z is {expectedHours - missingHours.Count}/{expectedHours} hours complete — " +
           $"missing {DescribeMissingHours(missingHours)}{outOfRangeNote} — will resume next cycle.";

    /// <summary>
    /// Renders missing forecast hours as compact ranges — e.g. <c>f113-f114, f117</c> —
    /// so an incomplete run says <em>which</em> hours are absent rather than only how many.
    /// </summary>
    /// <remarks>
    /// Output is capped at <paramref name="maxRanges"/> ranges because a wholly-unfetched
    /// run would otherwise emit an unbounded log line; the remainder is summarised.
    /// </remarks>
    /// <param name="missing">Missing forecast hours. Need not be sorted or distinct.</param>
    /// <param name="maxRanges">Maximum ranges to render before summarising the remainder.</param>
    /// <returns>A human-readable description. Never empty; returns "none" for an empty input.</returns>
    internal static string DescribeMissingHours(IReadOnlyList<int> missing, int maxRanges = 8)
    {
        if (missing.Count == 0) return "none";

        var sorted = missing.Distinct().OrderBy(h => h).ToList();
        var ranges = new List<(int Start, int End)>();

        var start = sorted[0];
        var prev = start;

        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == prev + 1)
            {
                prev = sorted[i];
                continue;
            }

            ranges.Add((start, prev));
            start = prev = sorted[i];
        }

        ranges.Add((start, prev));

        var shown = string.Join(", ", ranges
            .Take(maxRanges)
            .Select(r => r.Start == r.End ? $"f{r.Start:D3}" : $"f{r.Start:D3}-f{r.End:D3}"));

        var hidden = ranges.Count - maxRanges;
        return hidden > 0 ? $"{shown}, +{hidden} more range(s)" : shown;
    }

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
            var runTime = now.Date.AddHours(cycle);
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
                ModelRunUtc = modelRunUtc,
                ForecastHour = forecastHour,
                Lat = group.Key.Lat,
                Lon = group.Key.Lon,
            };

            if (byKey.TryGetValue("TMP:2 m above ground", out var tmp))
                point.TmpC = tmp.Value - 273.15f;

            if (byKey.TryGetValue("SPFH:2 m above ground", out var spfh))
                point.DwpC = SpfhToDewPointC(spfh.Value);

            if (byKey.TryGetValue("UGRD:10 m above ground", out var ugrd))
                point.UGrdMs = ugrd.Value;

            if (byKey.TryGetValue("VGRD:10 m above ground", out var vgrd))
                point.VGrdMs = vgrd.Value;

            if (byKey.TryGetValue("PRATE:surface", out var prate))
                point.PRateKgM2s = prate.Value;

            if (byKey.TryGetValue("TCDC:entire atmosphere", out var tcdc))
                point.TcdcPct = tcdc.Value;

            if (byKey.TryGetValue("CAPE:surface", out var cape))
                point.CapeJKg = cape.Value;

            if (byKey.TryGetValue("PRMSL:mean sea level", out var prmsl))
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
    /// status, so an in-progress run counts toward the total.
    /// <para>
    /// <b>At <c>retainCount</c> 1 the "older than the latest complete run" filter below is
    /// load-bearing rather than belt-and-braces.</b>  With one run retained, the moment a new
    /// run is registered the count check passes through and <c>Skip(retainCount)</c> selects
    /// the previous — complete — run.  Only the <c>r &lt; latestComplete</c> test saves it, because that run *is* the
    /// latest complete one and is therefore not older than itself.  Remove or weaken that
    /// filter and the sole complete run is deleted as soon as a download begins, leaving
    /// consumers — which are told to read the newest run whose <c>IsComplete</c> is true —
    /// with no model data at all for the duration of every fetch.
    /// </para>
    /// <para>
    /// When <c>latestComplete</c> is null (no complete run exists yet) the filter empties the
    /// delete list entirely, so nothing is purged.  Fails safe in both directions.
    /// </para>
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