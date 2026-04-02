using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace WxViewer;

/// <summary>
/// Scans a directory for weather-map PNG files and raises a notification
/// when files appear or disappear.
/// </summary>
/// <remarks>
/// Analysis filename format:  synoptic_{label}_{yyyyMMdd}_{HH}.png<br/>
/// Forecast filename format:  forecast_{yyyyMMdd}_{HH}_f{NNN}.png
/// </remarks>
public sealed class MapFileScanner : IDisposable
{
    // synoptic_{label}_{yyyyMMdd}_{HH}.png
    private static readonly Regex AnalysisRegex = new(
        @"^synoptic_(?<label>.+?)_(?<date>\d{8})_(?<hour>\d{2})\.png$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // forecast_{yyyyMMdd}_{HH}_f{NNN}.png
    private static readonly Regex ForecastRegex = new(
        @"^forecast_(?<date>\d{8})_(?<hour>\d{2})_f(?<fh>\d{3})\.png$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string     _directory;
    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher?  _watcher;

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
        _directory  = directory;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Starts watching the directory for file changes.  Safe to call multiple
    /// times — disposes the previous watcher first.
    /// </summary>
    public void StartWatching()
    {
        _watcher?.Dispose();
        if (!Directory.Exists(_directory)) return;

        _watcher = new FileSystemWatcher(_directory, "*.png")
        {
            NotifyFilter          = NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents   = true,
        };

        _watcher.Created += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsRenamed;
        _watcher.Error   += OnWatcherError;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => DirectoryChanged?.Invoke(this, EventArgs.Empty));

    private void OnFsRenamed(object sender, RenamedEventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background,
            () => DirectoryChanged?.Invoke(this, EventArgs.Empty));

    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            StartWatching();
            DirectoryChanged?.Invoke(this, EventArgs.Empty);
        });

    /// <summary>
    /// Reads the directory and returns all recognised analysis maps grouped by
    /// region label, with each group's frames ordered oldest-first for animation.
    /// </summary>
    public List<AnalysisLabel> ScanAnalysis()
    {
        if (!Directory.Exists(_directory)) return [];

        var byLabel = new Dictionary<string, List<AnalysisMap>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(_directory, "synoptic_*.png"))
        {
            var name  = Path.GetFileName(path);
            var match = AnalysisRegex.Match(name);
            if (!match.Success) continue;

            if (!TryParseDateTime(match.Groups["date"].Value, match.Groups["hour"].Value,
                                  out var obsUtc)) continue;

            var labelName  = match.Groups["label"].Value;
            var frameLabel = $"{labelName}  {obsUtc:yyyy-MM-dd HH}Z";
            var map        = new AnalysisMap(obsUtc, path, frameLabel);

            if (!byLabel.TryGetValue(labelName, out var list))
                byLabel[labelName] = list = [];
            list.Add(map);
        }

        var labels = new List<AnalysisLabel>();
        foreach (var (name, frames) in byLabel)
        {
            frames.Sort((a, b) => a.ObsUtc.CompareTo(b.ObsUtc)); // oldest-first for animation
            labels.Add(new AnalysisLabel(name, frames, name));
        }

        labels.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return labels;
    }

    /// <summary>
    /// Reads the directory and returns all recognised GFS model runs, each
    /// containing its frames ordered by forecast hour.  Runs are ordered
    /// newest-first.
    /// </summary>
    public List<ForecastRun> ScanForecasts()
    {
        if (!Directory.Exists(_directory)) return [];

        var byRun = new Dictionary<DateTime, List<ForecastFrame>>();

        foreach (var path in Directory.EnumerateFiles(_directory, "forecast_*.png"))
        {
            var name  = Path.GetFileName(path);
            var match = ForecastRegex.Match(name);
            if (!match.Success) continue;

            if (!TryParseDateTime(match.Groups["date"].Value, match.Groups["hour"].Value,
                                  out var runUtc)) continue;

            if (!int.TryParse(match.Groups["fh"].Value, out var fh)) continue;

            var validUtc  = runUtc.AddHours(fh);
            var hourLabel = $"+{fh:D3}h  Valid: {validUtc:yyyy-MM-dd HH}Z";
            var frame     = new ForecastFrame(fh, validUtc, path, hourLabel);

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

    /// <summary>Disposes the underlying FileSystemWatcher.</summary>
    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
