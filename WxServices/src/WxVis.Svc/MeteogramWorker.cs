using System.Text.Json;
using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using WxServices.Common;
using WxServices.Logging;

namespace WxVis.Svc;

/// <summary>
/// Background worker that renders GFS forecast meteograms for every unique
/// recipient location after each complete GFS model run.
/// </summary>
/// <remarks>
/// <para>
/// Polls <c>GfsModelRuns</c> on the same interval as
/// <see cref="ForecastMapWorker"/> (<c>WxVis:ForecastPollIntervalSeconds</c>).
/// When a new complete run is detected, one pair of meteogram PNGs (24-hour and
/// full-period) is rendered per unique <c>(MetarIcao, TempUnit)</c> combination
/// found in the <c>Recipients</c> table.
/// </para>
/// <para>
/// After all locations are rendered a manifest JSON file is written to the
/// output directory.  <c>WxViewer</c> reads this manifest to populate the
/// Meteograms tab, and <c>WxReport.Svc</c> reads it to locate the 24-hour PNG
/// to embed in each recipient's weather report email.
/// </para>
/// </remarks>
public sealed class MeteogramWorker : BackgroundService
{
    private readonly IConfiguration                        _config;
    private readonly DbContextOptions<WeatherDataContext>  _dbOptions;
    private Dictionary<string, string> _pythonEnv = new();

    // Model runs for which rendering is complete (all recipient locations done).
    private readonly HashSet<DateTime> _completedRuns = new();

    /// <summary>Initialises a new instance with the application configuration and DB options.</summary>
    /// <param name="config">Application configuration used to read <c>WxVis:*</c> settings each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/>.</param>
    public MeteogramWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config    = config;
        _dbOptions = dbOptions;
    }

    /// <summary>
    /// Polls for newly complete GFS model runs and renders meteograms for every
    /// configured recipient location that has not yet been rendered for that run.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host shuts down.</param>
    /// <sideeffects>Shells out to Python via <see cref="MapRenderer.RunAsync"/>. Writes manifest JSON. Writes log entries.</sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("MeteogramWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = LoadConfig();
                await RenderPendingAsync(cfg, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.Error("MeteogramWorker: unhandled exception.", ex);
            }

            var cfg2 = LoadConfig();
            try { await Task.Delay(TimeSpan.FromSeconds(cfg2.ForecastPollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        Logger.Info("MeteogramWorker stopped.");
    }

    /// <summary>
    /// Finds the latest complete GFS run, determines which recipient locations
    /// still need meteograms, renders them, then writes the manifest JSON.
    /// </summary>
    /// <param name="cfg">Current <see cref="WxVisConfig"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RenderPendingAsync(WxVisConfig cfg, CancellationToken ct)
    {
        DateTime latestCompleteRun;
        List<RecipientLocation> locations;

        using (var ctx = new WeatherDataContext(_dbOptions))
        {
            var runUtc = await ctx.GfsModelRuns
                .Where(r => r.IsComplete)
                .OrderByDescending(r => r.ModelRunUtc)
                .Select(r => r.ModelRunUtc)
                .FirstOrDefaultAsync(ct);

            if (runUtc == default) return;
            latestCompleteRun = runUtc;

            if (_completedRuns.Contains(latestCompleteRun)) return;

            // Remove stale tracking entries for older runs.
            _completedRuns.RemoveWhere(r => r < latestCompleteRun);

            var recipientRows = await ctx.Recipients
                .Where(r => r.Latitude  != null
                         && r.Longitude != null
                         && r.MetarIcao != null)
                .Select(r => new
                {
                    r.MetarIcao,
                    r.LocalityName,
                    r.TempUnit,
                    r.Timezone,
                    Latitude  = r.Latitude!.Value,
                    Longitude = r.Longitude!.Value,
                })
                .ToListAsync(ct);

            locations = recipientRows.Select(r => new RecipientLocation
            {
                Icao         = r.MetarIcao!,
                LocalityName = r.LocalityName ?? FirstIcao(r.MetarIcao!),
                TempUnit     = r.TempUnit,
                Timezone     = r.Timezone,
                Latitude     = r.Latitude,
                Longitude    = r.Longitude,
            }).ToList();
        }

        if (locations.Count == 0)
        {
            Logger.Info("MeteogramWorker: no recipients with resolved locations — skipping.");
            _completedRuns.Add(latestCompleteRun);
            return;
        }

        // De-duplicate on first ICAO + TempUnit + Timezone; use first locality name found.
        var unique = locations
            .Select(l => l with { Icao = FirstIcao(l.Icao) })
            .GroupBy(l => (l.Icao, l.TempUnit, l.Timezone))
            .Select(g => g.First())
            .OrderBy(l => l.Icao)
            .ThenBy(l => l.Timezone)
            .ToList();

        Logger.Info($"MeteogramWorker: rendering meteograms for run {latestCompleteRun:yyyy-MM-dd HH}Z — " +
                    $"{unique.Count} location(s).");

        var runTag      = latestCompleteRun.ToString("yyyyMMdd_HH");
        var manifestEntries = new List<ManifestEntry>();
        var allOk       = true;

        foreach (var loc in unique)
        {
            if (ct.IsCancellationRequested) break;

            // Sanitize timezone for use in filenames: "America/Chicago" → "America-Chicago"
            var tzSafe      = loc.Timezone.Replace('/', '-');
            var fileAbbrev  = $"meteogram_{runTag}_{loc.Icao}_{tzSafe}_abbrev.png";
            var fileFull    = $"meteogram_{runTag}_{loc.Icao}_{tzSafe}_full.png";
            var pathAbbrev  = Path.Combine(cfg.OutputDir, fileAbbrev);
            var pathFull    = Path.Combine(cfg.OutputDir, fileFull);

            // Skip if both outputs already exist and are newer than the model run.
            if (File.Exists(pathAbbrev) && File.Exists(pathFull)
                && File.GetLastWriteTimeUtc(pathAbbrev) > latestCompleteRun
                && File.GetLastWriteTimeUtc(pathFull)   > latestCompleteRun)
            {
                Logger.Info($"MeteogramWorker: {loc.Icao}/{loc.Timezone} already rendered — skipping.");
                manifestEntries.Add(new ManifestEntry(loc.Icao, loc.LocalityName, loc.TempUnit, loc.Timezone, fileAbbrev, fileFull));
                continue;
            }

            Logger.Info($"MeteogramWorker: rendering {loc.Icao} ({loc.LocalityName}) " +
                        $"lat={loc.Latitude:F2} lon={loc.Longitude:F2} unit={loc.TempUnit} tz={loc.Timezone}...");

            var scriptArgs =
                $"--run {runTag} " +
                $"--lat {loc.Latitude:F4} " +
                $"--lon {loc.Longitude:F4} " +
                $"--icao {loc.Icao} " +
                $"--locality \"{loc.LocalityName}\" " +
                $"--temp-unit {loc.TempUnit} " +
                $"--tz \"{loc.Timezone}\" " +
                $"--out-abbrev \"{pathAbbrev}\" " +
                $"--out-full \"{pathFull}\"";

            var ok = await MapRenderer.RunAsync(
                cfg.CondaPythonExe, cfg.ScriptDir,
                "meteogram.py", scriptArgs,
                ct,
                _pythonEnv);

            if (ok)
            {
                manifestEntries.Add(new ManifestEntry(loc.Icao, loc.LocalityName, loc.TempUnit, loc.Timezone, fileAbbrev, fileFull));
            }
            else
            {
                Logger.Error($"MeteogramWorker: render failed for {loc.Icao} — will retry next poll.");
                allOk = false;
            }
        }

        if (manifestEntries.Count > 0)
            WriteManifest(cfg.OutputDir, latestCompleteRun, manifestEntries);

        if (allOk)
            _completedRuns.Add(latestCompleteRun);
    }

    /// <summary>
    /// Writes the meteogram manifest JSON file to the output directory.
    /// The file is named <c>meteogram_manifest_{yyyyMMdd_HH}.json</c>.
    /// </summary>
    /// <param name="outputDir">Directory where PNG files and the manifest are written.</param>
    /// <param name="modelRun">GFS model run UTC timestamp.</param>
    /// <param name="entries">Manifest entries to serialise.</param>
    /// <sideeffects>Writes a JSON file to disk.</sideeffects>
    private static void WriteManifest(string outputDir, DateTime modelRun, List<ManifestEntry> entries)
    {
        var path = Path.Combine(outputDir, $"meteogram_manifest_{modelRun:yyyyMMdd_HH}.json");
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        Logger.Info($"MeteogramWorker: wrote manifest → {path}");
    }

    /// <summary>Returns the first ICAO identifier from a comma-separated list.</summary>
    private static string FirstIcao(string metarIcao)
        => metarIcao.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>Loads and returns the current <see cref="WxVisConfig"/>.</summary>
    private WxVisConfig LoadConfig()
    {
        var cfg = new WxVisConfig();
        _config.GetSection("WxVis").Bind(cfg);
        var paths = new WxPaths(_config["InstallRoot"]);
        cfg.ApplyPaths(paths);
        _pythonEnv = cfg.BuildPythonEnv(
            _config.GetConnectionString("WeatherData") ?? "", paths.LogsDir);
        return cfg;
    }

    // ── Private data types ────────────────────────────────────────────────────

    /// <summary>Intermediate record used to hold resolved recipient location data.</summary>
    private record RecipientLocation
    {
        public string  Icao                { get; init; } = "";
        public string  LocalityName        { get; init; } = "";
        public string  TempUnit            { get; init; } = "F";
        public string  Timezone            { get; init; } = "UTC";
        public double  Latitude            { get; init; }
        public double  Longitude           { get; init; }
    }

    /// <summary>One entry in the meteogram manifest JSON file.</summary>
    private record ManifestEntry(
        string Icao,
        string LocalityName,
        string TempUnit,
        string Timezone,
        string FileAbbrev,
        string FileFull);
}
