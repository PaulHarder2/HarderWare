using System.Globalization;
using System.Text;
using System.Text.Json;

using MetarParser.Data;
using MetarParser.Data.Entities;

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
/// full-period) is rendered per unique <c>(MetarIcao, TempUnit, Timezone, Language)</c>
/// combination found in the <c>Recipients</c> table — the language axis (WX-224)
/// so the in-image labels are localized, and only for languages in actual demand.
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
    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private Dictionary<string, string> _pythonEnv = new();

    // Model runs for which rendering is complete (all recipient locations done).
    private readonly HashSet<DateTime> _completedRuns = new();

    // WX-224: the in-image meteogram label tokens MeteogramWorker resolves per language.
    private static readonly string[] MeteogramTokenNames =
        { MeteogramTokens.Wind, MeteogramTokens.Rh, MeteogramTokens.Temp };

    /// <summary>Initialises a new instance with the application configuration and DB options.</summary>
    /// <param name="config">Application configuration used to read <c>WxVis:*</c> settings each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/>.</param>
    public MeteogramWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config = config;
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
        Dictionary<string, LangLabels> langData;

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

            // WX-224: resolve each recipient's report language so we render one meteogram per
            // language actually in demand at a location — not a blind per-language fan-out. This
            // mirrors ReportWorker.ResolveLanguageCode: a recipient's assigned LanguageId -> its
            // IsoCode, else the Report:DefaultLanguage fallback. ToIetfTag yields a 2-letter iso,
            // already canonical, so it matches ReportWorker's CanonicalIso-based manifest lookup.
            var langById = await ctx.Languages.ToDictionaryAsync(l => l.Id, ct);
            var defaultIso = LanguageHelper.ToIetfTag(_config["Report:DefaultLanguage"]);

            var recipientRows = await ctx.Recipients
                .Where(r => r.Latitude != null
                         && r.Longitude != null
                         && r.MetarIcao != null)
                .Select(r => new
                {
                    r.MetarIcao,
                    r.LocalityName,
                    r.TempUnit,
                    r.Timezone,
                    r.LanguageId,
                    Latitude = r.Latitude!.Value,
                    Longitude = r.Longitude!.Value,
                })
                .ToListAsync(ct);

            locations = recipientRows.Select(r => new RecipientLocation
            {
                Icao = r.MetarIcao!,
                LocalityName = r.LocalityName ?? FirstIcao(r.MetarIcao!),
                TempUnit = r.TempUnit,
                Timezone = r.Timezone,
                Language = ResolveIso(r.LanguageId, langById, defaultIso),
                Latitude = r.Latitude,
                Longitude = r.Longitude,
            }).ToList();

            // Load the in-image label tokens for every language in demand, plus each one's
            // CultureInfo-derived weekday abbreviations. Built once here, passed to meteogram.py
            // per render. wind/rh/temp come from LanguageTemplates (fail-soft: a missing token
            // omits its arg, so meteogram.py keeps the English default for that label); the day
            // abbreviations come from CultureInfo, not a token.
            var inDemand = locations.Select(l => l.Language).ToHashSet(StringComparer.Ordinal);
            var tokenRows = await ctx.LanguageTemplates
                .Where(t => MeteogramTokenNames.Contains(t.Token)
                         && t.Language != null
                         && inDemand.Contains(t.Language.IsoCode))
                .Select(t => new { Iso = t.Language!.IsoCode, t.Token, t.Phrase })
                .ToListAsync(ct);

            var cultureByIso = langById.Values
                .GroupBy(l => l.IsoCode, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().CultureName, StringComparer.Ordinal);

            langData = BuildLangData(
                inDemand,
                tokenRows.Select(t => (t.Iso, t.Token, t.Phrase)),
                cultureByIso);
        }

        if (locations.Count == 0)
        {
            Logger.Info("MeteogramWorker: no recipients with resolved locations — skipping.");
            _completedRuns.Add(latestCompleteRun);
            return;
        }

        // De-duplicate on first ICAO + TempUnit + Timezone + Language; use first locality name found.
        var unique = locations
            .Select(l => l with { Icao = FirstIcao(l.Icao) })
            .GroupBy(l => (l.Icao, l.TempUnit, l.Timezone, l.Language))
            .Select(g => g.First())
            .OrderBy(l => l.Icao)
            .ThenBy(l => l.Timezone)
            .ThenBy(l => l.Language, StringComparer.Ordinal)
            .ToList();

        Logger.Info($"MeteogramWorker: rendering meteograms for run {latestCompleteRun:yyyy-MM-dd HH}Z — " +
                    $"{unique.Count} location/language combo(s).");

        var runTag = latestCompleteRun.ToString("yyyyMMdd_HH");
        var manifestEntries = new List<ManifestEntry>();
        var allOk = true;

        foreach (var loc in unique)
        {
            if (ct.IsCancellationRequested) break;

            // Sanitize timezone for use in filenames: "America/Chicago" → "America-Chicago"
            var tzSafe = loc.Timezone.Replace('/', '-');
            var unitTag = loc.TempUnit.Equals("C", StringComparison.OrdinalIgnoreCase) ? "C" : "F";
            var fileAbbrev = $"meteogram_{runTag}_{loc.Icao}_{tzSafe}_{unitTag}_{loc.Language}_abbrev.png";
            var fileFull = $"meteogram_{runTag}_{loc.Icao}_{tzSafe}_{unitTag}_{loc.Language}_full.png";
            var pathAbbrev = Path.Combine(cfg.OutputDir, fileAbbrev);
            var pathFull = Path.Combine(cfg.OutputDir, fileFull);

            // Skip if both outputs already exist and are newer than the model run.
            if (File.Exists(pathAbbrev) && File.Exists(pathFull)
                && File.GetLastWriteTimeUtc(pathAbbrev) > latestCompleteRun
                && File.GetLastWriteTimeUtc(pathFull) > latestCompleteRun)
            {
                Logger.Info($"MeteogramWorker: {loc.Icao}/{loc.Timezone}/{loc.Language} already rendered — skipping.");
                manifestEntries.Add(new ManifestEntry(loc.Icao, loc.LocalityName, loc.TempUnit, loc.Timezone, loc.Language, fileAbbrev, fileFull));
                continue;
            }

            Logger.Info($"MeteogramWorker: rendering {loc.Icao} ({loc.LocalityName}) [{loc.Language}] " +
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
                $"--out-full \"{pathFull}\"" +
                BuildLabelArgs(langData, loc.Language);

            var ok = await MapRenderer.RunAsync(
                cfg.CondaPythonExe, cfg.ScriptDir,
                "meteogram.py", scriptArgs,
                ct,
                _pythonEnv);

            if (ok)
            {
                manifestEntries.Add(new ManifestEntry(loc.Icao, loc.LocalityName, loc.TempUnit, loc.Timezone, loc.Language, fileAbbrev, fileFull));
            }
            else
            {
                Logger.Error($"MeteogramWorker: render failed for {loc.Icao} [{loc.Language}] — will retry next poll.");
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

    /// <summary>
    /// The recipient's effective report-language ISO code: the assigned language's
    /// <see cref="Language.IsoCode"/>, or <paramref name="defaultIso"/> (the resolved
    /// <c>Report:DefaultLanguage</c>) when the recipient has none. Mirrors
    /// <c>ReportWorker.ResolveLanguageCode</c> so a recipient's meteogram language matches their
    /// report language — and thus the manifest lookup in <c>FindMeteogramAbbrevPath</c>.
    /// </summary>
    private static string ResolveIso(long? languageId, IReadOnlyDictionary<long, Language> langById, string defaultIso)
        => languageId is long id && langById.TryGetValue(id, out var lang)
            ? lang.IsoCode
            : defaultIso;

    /// <summary>
    /// Builds the per-language label set: the three in-image token phrases (wind/rh/temp, any of
    /// which may be absent → English default in meteogram.py) and the seven CultureInfo weekday
    /// abbreviations, for every ISO in demand.
    /// </summary>
    private static Dictionary<string, LangLabels> BuildLangData(
        IEnumerable<string> isos,
        IEnumerable<(string Iso, string Token, string Phrase)> tokenRows,
        IReadOnlyDictionary<string, string?> cultureByIso)
    {
        var byIsoToken = tokenRows
            .GroupBy(r => r.Iso, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(r => r.Token, r => r.Phrase, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var result = new Dictionary<string, LangLabels>(StringComparer.Ordinal);
        foreach (var iso in isos.Distinct(StringComparer.Ordinal))
        {
            byIsoToken.TryGetValue(iso, out var phrases);
            string? Get(string token) => phrases != null && phrases.TryGetValue(token, out var p) ? p : null;
            cultureByIso.TryGetValue(iso, out var cultureName);
            result[iso] = new LangLabels(
                Get(MeteogramTokens.Wind),
                Get(MeteogramTokens.Rh),
                Get(MeteogramTokens.Temp),
                DayAbbrevsFor(cultureName, iso));
        }
        return result;
    }

    /// <summary>
    /// The seven localized weekday abbreviations, Monday-first (index 0 = Monday, matching Python's
    /// <c>datetime.weekday()</c>), trailing period stripped. Culture is resolved by the
    /// <c>CultureName → IsoCode → Invariant</c> fallback chain so a missing/unsupported culture
    /// degrades to the invariant (English-ish) names without erroring (WX-224 acceptance).
    /// </summary>
    private static string[] DayAbbrevsFor(string? cultureName, string iso)
    {
        var culture = SafeCulture(cultureName) ?? SafeCulture(iso) ?? CultureInfo.InvariantCulture;
        var abbr = culture.DateTimeFormat.AbbreviatedDayNames; // .NET order: index 0 = Sunday
        var mondayFirst = new string[7];
        for (int i = 0; i < 7; i++)
            // Strip the trailing period some cultures use (es "lun."), plus any comma/quote, so the
            // value can't break the comma-joined, double-quoted --day-labels CLI arg (defensive —
            // real cultures have neither, but AbbreviatedDayNames is an open set).
            mondayFirst[i] = CliSafe(abbr[(i + (int)DayOfWeek.Monday) % 7].TrimEnd('.').Replace(",", ""));
        return mondayFirst;
    }

    /// <summary>A <see cref="CultureInfo"/> for <paramref name="name"/>, or <see langword="null"/> if unresolvable.</summary>
    private static CultureInfo? SafeCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return null; }
    }

    /// <summary>
    /// The localization CLI args for meteogram.py — <c>--label-wind/-rh/-temp</c> (each omitted when
    /// its token is absent, so the script keeps the English default) and <c>--day-labels</c> (the
    /// seven Monday-first weekday abbreviations). Values are quoted; phrases are migration-seeded
    /// vocabulary (no shell metacharacters by construction).
    /// </summary>
    private static string BuildLabelArgs(IReadOnlyDictionary<string, LangLabels> langData, string iso)
    {
        if (!langData.TryGetValue(iso, out var l)) return "";
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(l.Wind)) sb.Append($" --label-wind \"{CliSafe(l.Wind)}\"");
        if (!string.IsNullOrEmpty(l.Rh)) sb.Append($" --label-rh \"{CliSafe(l.Rh)}\"");
        if (!string.IsNullOrEmpty(l.Temp)) sb.Append($" --label-temp \"{CliSafe(l.Temp)}\"");
        if (l.DayAbbrevs.Length == 7) sb.Append($" --day-labels \"{string.Join(",", l.DayAbbrevs)}\"");
        return sb.ToString();
    }

    /// <summary>
    /// Strips the two characters that would corrupt a flat, double-quoted argv string passed to
    /// <see cref="MapRenderer.RunAsync"/>: the double quote itself and the backslash (which could
    /// escape the closing quote and shift later flags onto the wrong value). Chart labels and
    /// weekday abbreviations never legitimately contain either; this guards a hand-edited or
    /// generated <c>LanguageTemplates</c> phrase from breaking a language's render (WX-224 review).
    /// </summary>
    private static string CliSafe(string s) => s.Replace("\"", "").Replace("\\", "");

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
        public string Icao { get; init; } = "";
        public string LocalityName { get; init; } = "";
        public string TempUnit { get; init; } = "F";
        public string Timezone { get; init; } = "UTC";
        public string Language { get; init; } = "en";
        public double Latitude { get; init; }
        public double Longitude { get; init; }
    }

    /// <summary>The resolved in-image labels for one language: chart token phrases (any may be null) and the seven Monday-first weekday abbreviations.</summary>
    private sealed record LangLabels(string? Wind, string? Rh, string? Temp, string[] DayAbbrevs);

    /// <summary>One entry in the meteogram manifest JSON file.</summary>
    private record ManifestEntry(
        string Icao,
        string LocalityName,
        string TempUnit,
        string Timezone,
        string Language,
        string FileAbbrev,
        string FileFull);
}