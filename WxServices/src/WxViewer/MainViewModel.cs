using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WxViewer;

/// <summary>
/// ViewModel for the main WxViewer window.  Owns the file scanner, animation
/// timer, and all bindable state.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MapFileScanner  _scanner;
    private readonly DispatcherTimer _timer;

    // ── Backing fields ────────────────────────────────────────────────────────

    private AnalysisMap?  _selectedAnalysis;
    private BitmapImage?  _analysisImage;
    private ForecastRun?  _selectedRun;
    private int           _frameIndex;
    private int           _maxFrameIndex;
    private BitmapImage?  _forecastImage;
    private string        _frameLabel = "";
    private string        _statusText = "Ready";
    private bool          _isPlaying;
    private SpeedOption   _selectedSpeed;

    // ── Collections ───────────────────────────────────────────────────────────

    /// <summary>Available synoptic analysis maps, newest first.</summary>
    public ObservableCollection<AnalysisMap> AnalysisMaps { get; } = new();

    /// <summary>Available GFS forecast runs, newest first.</summary>
    public ObservableCollection<ForecastRun> ForecastRuns { get; } = new();

    /// <summary>Animation speed presets.</summary>
    public ObservableCollection<SpeedOption> SpeedOptions { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand PlayPauseCommand   { get; }
    public RelayCommand StepForwardCommand { get; }
    public RelayCommand StepBackCommand    { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the ViewModel, configures the file scanner and animation timer,
    /// and performs an initial directory scan.
    /// </summary>
    /// <param name="outputDir">Path to the plots output directory.</param>
    /// <param name="dispatcher">WPF UI dispatcher for marshalling scanner events.</param>
    public MainViewModel(string outputDir, Dispatcher dispatcher)
    {
        SpeedOptions.Add(new SpeedOption("Slow",   1500));
        SpeedOptions.Add(new SpeedOption("Medium",  800));
        SpeedOptions.Add(new SpeedOption("Fast",    300));
        _selectedSpeed = SpeedOptions[1];

        _timer          = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_selectedSpeed.IntervalMs) };
        _timer.Tick    += OnTimerTick;

        PlayPauseCommand   = new RelayCommand(TogglePlay,  () => _selectedRun?.Frames.Count > 1);
        StepForwardCommand = new RelayCommand(StepForward, () => _selectedRun?.Frames.Count > 0);
        StepBackCommand    = new RelayCommand(StepBack,    () => _selectedRun?.Frames.Count > 0);

        _scanner = new MapFileScanner(outputDir, dispatcher);
        _scanner.DirectoryChanged += (_, _) => Refresh();

        Refresh();
        _scanner.StartWatching();
    }

    // ── Analysis properties ───────────────────────────────────────────────────

    /// <summary>Currently selected analysis map; loading its image on change.</summary>
    public AnalysisMap? SelectedAnalysis
    {
        get => _selectedAnalysis;
        set
        {
            if (_selectedAnalysis == value) return;
            _selectedAnalysis = value;
            OnPropertyChanged();
            AnalysisImage = LoadBitmap(value?.FilePath);
            UpdateStatus();
        }
    }

    /// <summary>Decoded bitmap for the selected analysis map.</summary>
    public BitmapImage? AnalysisImage
    {
        get => _analysisImage;
        private set { _analysisImage = value; OnPropertyChanged(); }
    }

    // ── Forecast properties ───────────────────────────────────────────────────

    /// <summary>
    /// Currently selected GFS model run.  Changing this run resets the frame
    /// index to 0 and updates MaxFrameIndex before raising FrameIndex
    /// PropertyChanged, ensuring the Slider never sees an out-of-range value.
    /// </summary>
    public ForecastRun? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (_selectedRun == value) return;

            // Stop animation when switching runs.
            _timer.Stop();
            _isPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));

            _selectedRun = value;
            OnPropertyChanged();

            // Update Maximum before Value so the Slider's range is correct.
            MaxFrameIndex = (_selectedRun?.Frames.Count ?? 1) - 1;
            if (MaxFrameIndex < 0) MaxFrameIndex = 0;

            _frameIndex = 0;
            OnPropertyChanged(nameof(FrameIndex));
            LoadForecastImage();
        }
    }

    /// <summary>
    /// Current frame index (0-based).  The setter clamps to [0, MaxFrameIndex]
    /// and loads the corresponding forecast image.
    /// </summary>
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

    /// <summary>Maximum slider value — one less than the number of frames in the selected run.</summary>
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

    /// <summary>Human-readable label for the current forecast frame shown in the toolbar.</summary>
    public string FrameLabel
    {
        get => _frameLabel;
        private set { _frameLabel = value; OnPropertyChanged(); }
    }

    // ── Animation properties ──────────────────────────────────────────────────

    /// <summary><see langword="true"/> while the animation timer is running.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set { _isPlaying = value; OnPropertyChanged(); }
    }

    /// <summary>Selected animation speed; updating this property restarts the timer interval.</summary>
    public SpeedOption SelectedSpeed
    {
        get => _selectedSpeed;
        set
        {
            if (_selectedSpeed == value) return;
            _selectedSpeed  = value;
            _timer.Interval = TimeSpan.FromMilliseconds(value.IntervalMs);
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
        var prevAnalysisUtc = _selectedAnalysis?.ObsUtc;
        var prevRunUtc      = _selectedRun?.ModelRunUtc;

        var analysisList = _scanner.ScanAnalysis();
        AnalysisMaps.Clear();
        foreach (var m in analysisList) AnalysisMaps.Add(m);

        var forecastList = _scanner.ScanForecasts();
        ForecastRuns.Clear();
        foreach (var r in forecastList) ForecastRuns.Add(r);

        SelectedAnalysis = prevAnalysisUtc.HasValue
            ? analysisList.FirstOrDefault(m => m.ObsUtc == prevAnalysisUtc.Value)
              ?? analysisList.FirstOrDefault()
            : analysisList.FirstOrDefault();

        SelectedRun = prevRunUtc.HasValue
            ? forecastList.FirstOrDefault(r => r.ModelRunUtc == prevRunUtc.Value)
              ?? forecastList.FirstOrDefault()
            : forecastList.FirstOrDefault();

        UpdateStatus();
    }

    // ── Animation ─────────────────────────────────────────────────────────────

    /// <summary>Toggles the animation timer.  Restarts from frame 0 if currently at the last frame.</summary>
    public void TogglePlay()
    {
        if (_selectedRun?.Frames.Count is null or <= 1) return;

        if (_isPlaying)
        {
            _timer.Stop();
            IsPlaying = false;
        }
        else
        {
            if (_frameIndex >= _maxFrameIndex) FrameIndex = 0;
            _timer.Start();
            IsPlaying = true;
        }
    }

    /// <summary>Stops animation and advances one frame forward.</summary>
    public void StepForward()
    {
        if (_selectedRun?.Frames.Count is null or 0) return;
        StopAnimation();
        FrameIndex = Math.Min(_frameIndex + 1, _maxFrameIndex);
    }

    /// <summary>Stops animation and steps one frame back.</summary>
    public void StepBack()
    {
        if (_selectedRun?.Frames.Count is null or 0) return;
        StopAnimation();
        FrameIndex = Math.Max(_frameIndex - 1, 0);
    }

    private void StopAnimation()
    {
        _timer.Stop();
        IsPlaying = false;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_frameIndex >= _maxFrameIndex)
        {
            _timer.Stop();
            IsPlaying = false;
            return;
        }
        // Increment directly to bypass the clamp-and-compare in the setter.
        _frameIndex++;
        OnPropertyChanged(nameof(FrameIndex));
        LoadForecastImage();
    }

    // ── Image loading ─────────────────────────────────────────────────────────

    private void LoadForecastImage()
    {
        var frame = CurrentFrame;
        ForecastImage = LoadBitmap(frame?.FilePath);
        FrameLabel    = frame?.HourLabel ?? "";
        UpdateStatus();
    }

    private ForecastFrame? CurrentFrame =>
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

        if (_selectedAnalysis is not null)
            parts.Add($"Analysis: {_selectedAnalysis.Label}");

        if (CurrentFrame is { } frame)
            parts.Add(frame.HourLabel);
        else if (_selectedRun is not null)
            parts.Add($"Run: {_selectedRun.Label}");

        StatusText = parts.Count > 0 ? string.Join("  |  ", parts) : "No maps loaded";
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>Stops the animation timer and disposes the file scanner.</summary>
    public void Dispose()
    {
        _timer.Stop();
        _scanner.Dispose();
    }
}
