using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

using WxServices.Logging;

namespace WxViewer;

/// <summary>
/// Scans a directory for weather-map PNG files and raises a notification
/// when files appear or disappear.
/// </summary>
/// <remarks>
/// Analysis filename format:   synoptic_{label}_{yyyyMMdd}_{HH}_z{N}.png<br/>
/// Forecast filename format:   forecast_{yyyyMMdd}_{HH}_f{NNN}_z{N}.png<br/>
/// Meteogram manifest format:  meteogram_manifest_{yyyyMMdd}_{HH}.json<br/>
/// Meteogram PNG format:       meteogram_{yyyyMMdd}_{HH}_{ICAO}_{F|C}_{abbrev|full}.png
/// </remarks>
public sealed class MapFileScanner : IDisposable
{
    // synoptic_{label}_{yyyyMMdd}_{HH}_z{N}.png
    private static readonly Regex AnalysisRegex = new(
        @"^synoptic_(?<label>.+?)_(?<date>\d{8})_(?<hour>\d{2})_z(?<zoom>\d+)\.png$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // forecast_{yyyyMMdd}_{HH}_f{NNN}_z{N}.png
    private static readonly Regex ForecastRegex = new(
        @"^forecast_(?<date>\d{8})_(?<hour>\d{2})_f(?<fh>\d{3})_z(?<zoom>\d+)\.png$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // meteogram_manifest_{yyyyMMdd}_{HH}.json
    private static readonly Regex ManifestRegex = new(
        @"^meteogram_manifest_(?<date>\d{8})_(?<hour>\d{2})\.json$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _directory;
    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _pngWatcher;
    private FileSystemWatcher? _jsonWatcher;

    /// <summary>
    /// Raised on the UI thread when PNG files are created, deleted, or renamed
    /// in the watched directory.
    /// </summary>
    public event EventHandler? DirectoryChanged;

    /// <summary>Initialises the scanner for the given directory.</summary>
    /// <param name="directory">The plots output directory to scan and watch.</param>
    /// <param name="dispatcher">The WPF UI dispatcher used to marshal change events.</param>
    public MapFileScanner(string directory, Dispatcher dispatcher)
    {
        _directory = directory;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Starts watching the directory for file changes.  Safe to call multiple
    /// times — disposes the previous watchers first.  Watches both PNG files
    /// (maps and meteogram images) and JSON files (meteogram manifests).
    /// </summary>
    public void StartWatching()
    {
        _pngWatcher?.Dispose();
        _jsonWatcher?.Dispose();
        if (!Directory.Exists(_directory)) return;

        _pngWatcher = MakeWatcher("*.png");
        _jsonWatcher = MakeWatcher("*.json");
    }

    private FileSystemWatcher MakeWatcher(string filter)
    {
        var w = new FileSystemWatcher(_directory, filter)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        w.Created += OnFsEvent;
        w.Deleted += OnFsEvent;
        w.Renamed += OnFsRenamed;
        w.Error += OnWatcherError;
        return w;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => DirectoryChanged?.Invoke(this, EventArgs.Empty));

    private void OnFsRenamed(object sender, RenamedEventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => DirectoryChanged?.Invoke(this, EventArgs.Empty));

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        Logger.Warn($"FileSystemWatcher error; restarting watchers.", ex);
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            StartWatching();
            DirectoryChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Reads the directory and returns one <see cref="AnalysisLabel"/> per
    /// unique observation time, with zoom-level paths collected on each
    /// <see cref="AnalysisMap"/>.  Sorted newest-first.
    /// </summary>
    public List<AnalysisLabel> ScanAnalysis()
    {
        if (!Directory.Exists(_directory)) return [];

        // Key: (label, obsUtc).  Value: zoom → path.
        var groups = new Dictionary<(string label, DateTime obsUtc), Dictionary<int, string>>();

        foreach (var path in Directory.EnumerateFiles(_directory, "synoptic_*.png"))
        {
            var name = Path.GetFileName(path);
            var match = AnalysisRegex.Match(name);
            if (!match.Success) continue;

            if (!TryParseDateTime(match.Groups["date"].Value, match.Groups["hour"].Value,
                                  out var obsUtc)) continue;
            if (!int.TryParse(match.Groups["zoom"].Value, out var zoom)) continue;

            var label = match.Groups["label"].Value;
            var key = (label, obsUtc);

            if (!groups.TryGetValue(key, out var zoomPaths))
                groups[key] = zoomPaths = new Dictionary<int, string>();
            zoomPaths[zoom] = path;
        }

        var labels = new List<AnalysisLabel>();
        foreach (var ((label, obsUtc), zoomPaths) in groups)
        {
            if (!zoomPaths.TryGetValue(1, out var z1Path)) continue; // require z1
            var displayLabel = $"{obsUtc:yyyy-MM-dd HH}Z";
            var map = new AnalysisMap(obsUtc, z1Path, displayLabel, zoomPaths);
            labels.Add(new AnalysisLabel(z1Path, [map], displayLabel));
        }

        labels.Sort((a, b) => b.Frames[0].ObsUtc.CompareTo(a.Frames[0].ObsUtc)); // newest-first
        return labels;
    }

    /// <summary>
    /// Reads the directory and returns all recognised GFS model runs, each
    /// containing its frames ordered by forecast hour, with zoom-level paths
    /// collected on each <see cref="ForecastFrame"/>.  Runs are ordered newest-first.
    /// </summary>
    public List<ForecastRun> ScanForecasts()
    {
        if (!Directory.Exists(_directory)) return [];

        // Key: (runUtc, forecastHour).  Value: zoom → path.
        var zoomGroups = new Dictionary<(DateTime runUtc, int fh), Dictionary<int, string>>();

        foreach (var path in Directory.EnumerateFiles(_directory, "forecast_*.png"))
        {
            var name = Path.GetFileName(path);
            var match = ForecastRegex.Match(name);
            if (!match.Success) continue;

            if (!TryParseDateTime(match.Groups["date"].Value, match.Groups["hour"].Value,
                                  out var runUtc)) continue;
            if (!int.TryParse(match.Groups["fh"].Value, out var fh)) continue;
            if (!int.TryParse(match.Groups["zoom"].Value, out var zoom)) continue;

            var key = (runUtc, fh);
            if (!zoomGroups.TryGetValue(key, out var zoomPaths))
                zoomGroups[key] = zoomPaths = new Dictionary<int, string>();
            zoomPaths[zoom] = path;
        }

        // Group frames by model run.
        var byRun = new Dictionary<DateTime, List<ForecastFrame>>();
        foreach (var ((runUtc, fh), zoomPaths) in zoomGroups)
        {
            if (!zoomPaths.TryGetValue(1, out var z1Path)) continue; // require z1
            var validUtc = runUtc.AddHours(fh);
            var hourLabel = $"+{fh:D3}h  Valid: {validUtc:yyyy-MM-dd HH}Z";
            var frame = new ForecastFrame(fh, validUtc, z1Path, hourLabel, zoomPaths);

            if (!byRun.TryGetValue(runUtc, out var list))
                byRun[runUtc] = list = [];
            list.Add(frame);
        }

        var runs = new List<ForecastRun>();
        foreach (var (runUtc, frames) in byRun)
        {
            frames.Sort((a, b) => a.ForecastHour.CompareTo(b.ForecastHour));
            runs.Add(new ForecastRun(runUtc, frames, $"GFS  {runUtc:yyyy-MM-dd HH}Z"));
        }

        runs.Sort((a, b) => b.ModelRunUtc.CompareTo(a.ModelRunUtc));
        return runs;
    }

    private static bool TryParseDateTime(string datePart, string hourPart, out DateTime result)
    {
        result = default;
        if (!DateTime.TryParseExact(datePart, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        if (!int.TryParse(hourPart, out var h) || h < 0 || h > 23) return false;
        result = DateTime.SpecifyKind(d.AddHours(h), DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// Reads the directory for meteogram manifest JSON files and returns one
    /// <see cref="MeteogramRun"/> per manifest, sorted newest-first.
    /// Each run's items are sorted by ICAO.
    /// </summary>
    public List<MeteogramRun> ScanMeteograms()
    {
        if (!Directory.Exists(_directory)) return [];

        var runs = new List<MeteogramRun>();

        foreach (var path in Directory.EnumerateFiles(_directory, "meteogram_manifest_*.json"))
        {
            var name = Path.GetFileName(path);
            var match = ManifestRegex.Match(name);
            if (!match.Success) continue;

            if (!TryParseDateTime(match.Groups["date"].Value, match.Groups["hour"].Value,
                                  out var runUtc)) continue;

            var items = ParseManifest(path, _directory);
            if (items.Count == 0) continue;

            var label = $"GFS  {runUtc:yyyy-MM-dd HH}Z";
            runs.Add(new MeteogramRun(runUtc, label, items));
        }

        runs.Sort((a, b) => b.ModelRunUtc.CompareTo(a.ModelRunUtc)); // newest-first
        return runs;
    }

    private static List<MeteogramItem> ParseManifest(string manifestPath, string directory)
    {
        var items = new List<MeteogramItem>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("Icao", out var icaoProp)) continue;
                if (!entry.TryGetProperty("LocalityName", out var localityProp)) continue;
                if (!entry.TryGetProperty("TempUnit", out var unitProp)) continue;
                if (!entry.TryGetProperty("Timezone", out var tzProp)) continue;
                if (!entry.TryGetProperty("FileFull", out var fileProp)) continue;

                var icao = icaoProp.GetString();
                var locality = localityProp.GetString();
                var unit = unitProp.GetString();
                var tz = tzProp.GetString();
                var file = fileProp.GetString();

                if (string.IsNullOrWhiteSpace(icao) || string.IsNullOrWhiteSpace(file)) continue;

                var fullPath = Path.Combine(directory, file);
                items.Add(new MeteogramItem(
                    icao,
                    locality ?? icao,
                    unit ?? "F",
                    tz ?? "UTC",
                    fullPath));
            }
        }
        catch (Exception ex) { Logger.Warn($"Failed to parse manifest: {Path.GetFileName(manifestPath)}", ex); }

        items.Sort((a, b) => string.Compare(a.Icao, b.Icao, StringComparison.Ordinal));
        return items;
    }

    /// <summary>Disposes the underlying FileSystemWatchers.</summary>
    public void Dispose()
    {
        _pngWatcher?.Dispose();
        _pngWatcher = null;
        _jsonWatcher?.Dispose();
        _jsonWatcher = null;
    }
}