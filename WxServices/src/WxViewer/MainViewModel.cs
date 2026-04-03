using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WxViewer;

/// <summary>
/// ViewModel for the main WxViewer window.  Owns the file scanner, two
/// independent animation timers (analysis and forecast), and all bindable state.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MapFileScanner  _scanner;
    private readonly DispatcherTimer _analysisTimer;
    private readonly DispatcherTimer _forecastTimer;

    // ── Analysis backing fields ───────────────────────────────────────────────

    private AnalysisLabel? _selectedAnalysisLabel;
    private int            _analysisFrameIndex;
    private int            _maxAnalysisFrameIndex;
    private BitmapImage?   _analysisImage;
    private string         _analysisFrameLabel = "";
    private bool           _isAnalysisPlaying;
    private SpeedOption    _selectedAnalysisSpeed;

    // ── Forecast backing fields ───────────────────────────────────────────────

    private ForecastRun?   _selectedRun;
    private int            _frameIndex;
    private int            _maxFrameIndex;
    private BitmapImage?   _forecastImage;
    private string         _frameLabel = "";
    private bool           _isPlaying;
    private SpeedOption    _selectedForecastSpeed;

    // ── Shared ────────────────────────────────────────────────────────────────

    private string _statusText = "Ready";

    // ── Collections ───────────────────────────────────────────────────────────

    /// <summary>Available analysis region labels, sorted alphabetically.</summary>
    public ObservableCollection<AnalysisLabel> AnalysisLabels { get; } = new();

    /// <summary>Available GFS forecast runs, newest first.</summary>
    public ObservableCollection<ForecastRun> ForecastRuns { get; } = new();

    /// <summary>Animation speed presets shared by both panes.</summary>
    public ObservableCollection<SpeedOption> SpeedOptions { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand AnalysisPlayPauseCommand   { get; }
    public RelayCommand AnalysisStepForwardCommand { get; }
    public RelayCommand AnalysisStepBackCommand    { get; }

    public RelayCommand PlayPauseCommand   { get; }
    public RelayCommand StepForwardCommand { get; }
    public RelayCommand StepBackCommand    { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the ViewModel, configures both file scanner and animation timers,
    /// and performs an initial directory scan.
    /// </summary>
    /// <param name="outputDir">Path to the plots output directory.</param>
    /// <param name="dispatcher">WPF UI dispatcher for marshalling scanner events.</param>
    public MainViewModel(string outputDir, Dispatcher dispatcher)
    {
        SpeedOptions.Add(new SpeedOption("Slow",   1500));
        SpeedOptions.Add(new SpeedOption("Medium",  800));
        SpeedOptions.Add(new SpeedOption("Fast",    300));
        _selectedAnalysisSpeed = SpeedOptions[1];
        _selectedForecastSpeed = SpeedOptions[1];

        _analysisTimer        = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_selectedAnalysisSpeed.IntervalMs) };
        _analysisTimer.Tick  += OnAnalysisTimerTick;

        _forecastTimer        = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_selectedForecastSpeed.IntervalMs) };
        _forecastTimer.Tick  += OnForecastTimerTick;

        AnalysisPlayPauseCommand   = new RelayCommand(ToggleAnalysisPlay,  () => AnalysisLabels.Count > 1);
        AnalysisStepForwardCommand = new RelayCommand(StepAnalysisForward, () => AnalysisLabels.Count > 0);
        AnalysisStepBackCommand    = new RelayCommand(StepAnalysisBack,    () => AnalysisLabels.Count > 0);

        PlayPauseCommand   = new RelayCommand(TogglePlay,  () => _selectedRun?.Frames.Count > 1);
        StepForwardCommand = new RelayCommand(StepForward, () => _selectedRun?.Frames.Count > 0);
        StepBackCommand    = new RelayCommand(StepBack,    () => _selectedRun?.Frames.Count > 0);

        _scanner = new MapFileScanner(outputDir, dispatcher);
        _scanner.DirectoryChanged += (_, _) => Refresh();

        Refresh();
        _scanner.StartWatching();
    }

    // ── Analysis properties ───────────────────────────────────────────────────

    /// <summary>Currently selected analysis region label.</summary>
    public AnalysisLabel? SelectedAnalysisLabel
    {
        get => _selectedAnalysisLabel;
        set
        {
            if (_selectedAnalysisLabel == value) return;

            _analysisTimer.Stop();
            _isAnalysisPlaying = false;
            OnPropertyChanged(nameof(IsAnalysisPlaying));

            _selectedAnalysisLabel = value;
            OnPropertyChanged();

            // Sync slider to this item's position in the list.
            var idx = value is null ? 0 : AnalysisLabels.IndexOf(value);
            _analysisFrameIndex = Math.Max(0, idx);
            OnPropertyChanged(nameof(AnalysisFrameIndex));
            LoadAnalysisImage();
        }
    }

    /// <summary>Current analysis frame index (0 = oldest).</summary>
    public int AnalysisFrameIndex
    {
        get => _analysisFrameIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, _maxAnalysisFrameIndex);
            if (_analysisFrameIndex == clamped) return;
            _analysisFrameIndex = clamped;
            OnPropertyChanged();
            _selectedAnalysisLabel = AnalysisLabels.Count > 0 ? AnalysisLabels[_analysisFrameIndex] : null;
            OnPropertyChanged(nameof(SelectedAnalysisLabel));
            LoadAnalysisImage();
        }
    }

    /// <summary>Maximum slider value for the analysis pane.</summary>
    public int MaxAnalysisFrameIndex
    {
        get => _maxAnalysisFrameIndex;
        private set { _maxAnalysisFrameIndex = value; OnPropertyChanged(); }
    }

    /// <summary>Decoded bitmap for the current analysis frame.</summary>
    public BitmapImage? AnalysisImage
    {
        get => _analysisImage;
        private set { _analysisImage = value; OnPropertyChanged(); }
    }

    /// <summary>Human-readable obs-time label for the current analysis frame.</summary>
    public string AnalysisFrameLabel
    {
        get => _analysisFrameLabel;
        private set { _analysisFrameLabel = value; OnPropertyChanged(); }
    }

    /// <summary><see langword="true"/> while the analysis animation timer is running.</summary>
    public bool IsAnalysisPlaying
    {
        get => _isAnalysisPlaying;
        private set { _isAnalysisPlaying = value; OnPropertyChanged(); }
    }

    /// <summary>Selected animation speed for the analysis pane.</summary>
    public SpeedOption SelectedAnalysisSpeed
    {
        get => _selectedAnalysisSpeed;
        set
        {
            if (_selectedAnalysisSpeed == value) return;
            _selectedAnalysisSpeed    = value;
            _analysisTimer.Interval   = TimeSpan.FromMilliseconds(value.IntervalMs);
            OnPropertyChanged();
        }
    }

    // ── Forecast properties ───────────────────────────────────────────────────

    /// <summary>
    /// Currently selected GFS model run.  Changing the run stops animation,
    /// resets the frame index, and updates MaxFrameIndex before raising
    /// FrameIndex PropertyChanged so the Slider never sees an out-of-range value.
    /// </summary>
    public ForecastRun? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (_selectedRun == value) return;

            _forecastTimer.Stop();
            _isPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));

            _selectedRun = value;
            OnPropertyChanged();

            MaxFrameIndex = (_selectedRun?.Frames.Count ?? 1) - 1;
            if (MaxFrameIndex < 0) MaxFrameIndex = 0;

            _frameIndex = 0;
            OnPropertyChanged(nameof(FrameIndex));
            LoadForecastImage();
        }
    }

    /// <summary>Current forecast frame index (0-based).</summary>
    public int FrameIndex
    {
        get => _frameIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, _maxFrameIndex);
            if (_frameIndex == clamped) return;
            _frameIndex = clamped;
            OnPropertyChanged();
            LoadForecastImage();
        }
    }

    /// <summary>Maximum slider value for the forecast pane.</summary>
    public int MaxFrameIndex
    {
        get => _maxFrameIndex;
        private set { _maxFrameIndex = value; OnPropertyChanged(); }
    }

    /// <summary>Decoded bitmap for the current forecast frame.</summary>
    public BitmapImage? ForecastImage
    {
        get => _forecastImage;
        private set { _forecastImage = value; OnPropertyChanged(); }
    }

    /// <summary>Human-readable label for the current forecast frame.</summary>
    public string FrameLabel
    {
        get => _frameLabel;
        private set { _frameLabel = value; OnPropertyChanged(); }
    }

    /// <summary><see langword="true"/> while the forecast animation timer is running.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set { _isPlaying = value; OnPropertyChanged(); }
    }

    /// <summary>Selected animation speed for the forecast pane.</summary>
    public SpeedOption SelectedForecastSpeed
    {
        get => _selectedForecastSpeed;
        set
        {
            if (_selectedForecastSpeed == value) return;
            _selectedForecastSpeed   = value;
            _forecastTimer.Interval  = TimeSpan.FromMilliseconds(value.IntervalMs);
            OnPropertyChanged();
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>Status bar text.</summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Rescans the plots directory and repopulates the collections,
    /// preserving current selections where possible.
    /// </summary>
    public void Refresh()
    {
        var prevLabelName = _selectedAnalysisLabel?.Name;
        var prevRunUtc    = _selectedRun?.ModelRunUtc;

        var analysisList = _scanner.ScanAnalysis();
        AnalysisLabels.Clear();
        foreach (var l in analysisList) AnalysisLabels.Add(l);
        MaxAnalysisFrameIndex = Math.Max(0, AnalysisLabels.Count - 1);

        var forecastList = _scanner.ScanForecasts();
        ForecastRuns.Clear();
        foreach (var r in forecastList) ForecastRuns.Add(r);

        SelectedAnalysisLabel = prevLabelName is not null
            ? analysisList.FirstOrDefault(l => l.Name == prevLabelName) ?? analysisList.LastOrDefault()
            : analysisList.LastOrDefault();

        SelectedRun = prevRunUtc.HasValue
            ? forecastList.FirstOrDefault(r => r.ModelRunUtc == prevRunUtc.Value) ?? forecastList.FirstOrDefault()
            : forecastList.FirstOrDefault();

        UpdateStatus();
    }

    // ── Analysis animation ────────────────────────────────────────────────────

    /// <summary>Toggles the analysis animation.  Restarts from frame 0 if at the last frame.</summary>
    public void ToggleAnalysisPlay()
    {
        if (AnalysisLabels.Count <= 1) return;

        if (_isAnalysisPlaying)
        {
            _analysisTimer.Stop();
            IsAnalysisPlaying = false;
        }
        else
        {
            if (_analysisFrameIndex >= _maxAnalysisFrameIndex) AnalysisFrameIndex = 0;
            _analysisTimer.Start();
            IsAnalysisPlaying = true;
        }
    }

    /// <summary>Stops animation and steps one analysis frame forward.</summary>
    public void StepAnalysisForward()
    {
        if (AnalysisLabels.Count == 0) return;
        StopAnalysisAnimation();
        AnalysisFrameIndex = Math.Min(_analysisFrameIndex + 1, _maxAnalysisFrameIndex);
    }

    /// <summary>Stops animation and steps one analysis frame back.</summary>
    public void StepAnalysisBack()
    {
        if (AnalysisLabels.Count == 0) return;
        StopAnalysisAnimation();
        AnalysisFrameIndex = Math.Max(_analysisFrameIndex - 1, 0);
    }

    private void StopAnalysisAnimation()
    {
        _analysisTimer.Stop();
        IsAnalysisPlaying = false;
    }

    private void OnAnalysisTimerTick(object? sender, EventArgs e)
    {
        if (_analysisFrameIndex >= _maxAnalysisFrameIndex)
        {
            _analysisTimer.Stop();
            IsAnalysisPlaying = false;
            return;
        }
        _analysisFrameIndex++;
        _selectedAnalysisLabel = AnalysisLabels[_analysisFrameIndex];
        OnPropertyChanged(nameof(AnalysisFrameIndex));
        OnPropertyChanged(nameof(SelectedAnalysisLabel));
        LoadAnalysisImage();
    }

    // ── Forecast animation ────────────────────────────────────────────────────

    /// <summary>Toggles the forecast animation.  Restarts from frame 0 if at the last frame.</summary>
    public void TogglePlay()
    {
        if (_selectedRun?.Frames.Count is null or <= 1) return;

        if (_isPlaying)
        {
            _forecastTimer.Stop();
            IsPlaying = false;
        }
        else
        {
            if (_frameIndex >= _maxFrameIndex) FrameIndex = 0;
            _forecastTimer.Start();
            IsPlaying = true;
        }
    }

    /// <summary>Stops animation and steps one forecast frame forward.</summary>
    public void StepForward()
    {
        if (_selectedRun?.Frames.Count is null or 0) return;
        StopForecastAnimation();
        FrameIndex = Math.Min(_frameIndex + 1, _maxFrameIndex);
    }

    /// <summary>Stops animation and steps one forecast frame back.</summary>
    public void StepBack()
    {
        if (_selectedRun?.Frames.Count is null or 0) return;
        StopForecastAnimation();
        FrameIndex = Math.Max(_frameIndex - 1, 0);
    }

    private void StopForecastAnimation()
    {
        _forecastTimer.Stop();
        IsPlaying = false;
    }

    private void OnForecastTimerTick(object? sender, EventArgs e)
    {
        if (_frameIndex >= _maxFrameIndex)
        {
            _forecastTimer.Stop();
            IsPlaying = false;
            return;
        }
        _frameIndex++;
        OnPropertyChanged(nameof(FrameIndex));
        LoadForecastImage();
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    private void LoadAnalysisImage()
    {
        var frame = CurrentAnalysisFrame;
        AnalysisImage      = LoadBitmap(frame?.FilePath);
        AnalysisFrameLabel = frame is null ? "" : $"{frame.ObsUtc:yyyy-MM-dd HH}Z";
        UpdateStatus();
    }

    private AnalysisMap? CurrentAnalysisFrame =>
        _selectedAnalysisLabel?.Frames.Count > 0 ? _selectedAnalysisLabel.Frames[0] : null;

    private void LoadForecastImage()
    {
        var frame = CurrentForecastFrame;
        ForecastImage = LoadBitmap(frame?.FilePath);
        FrameLabel    = frame?.HourLabel ?? "";
        UpdateStatus();
    }

    private ForecastFrame? CurrentForecastFrame =>
        _selectedRun?.Frames.Count > 0
            ? _selectedRun.Frames[Math.Clamp(_frameIndex, 0, _selectedRun.Frames.Count - 1)]
            : null;

    /// <summary>
    /// Loads a PNG as a frozen BitmapImage using CacheOption.OnLoad so the
    /// file handle is released immediately after decoding.
    /// </summary>
    private static BitmapImage? LoadBitmap(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource     = new Uri(path, UriKind.Absolute);
            bmp.CacheOption   = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateStatus()
    {
        var parts = new List<string>();

        if (CurrentAnalysisFrame is { } af)
            parts.Add($"Analysis: {af.ObsUtc:yyyy-MM-dd HH}Z");

        if (CurrentForecastFrame is { } ff)
            parts.Add(ff.HourLabel);
        else if (_selectedRun is not null)
            parts.Add($"Run: {_selectedRun.Label}");

        StatusText = parts.Count > 0 ? string.Join("  |  ", parts) : "No maps loaded";
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Stops both animation timers and disposes the file scanner.</summary>
    public void Dispose()
    {
        _analysisTimer.Stop();
        _forecastTimer.Stop();
        _scanner.Dispose();
    }
}
