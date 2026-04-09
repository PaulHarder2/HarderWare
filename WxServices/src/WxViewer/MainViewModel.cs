using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.SqlClient;

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

    // ── Meteogram backing fields ──────────────────────────────────────────────

    private MeteogramRun?      _selectedMeteogramRun;
    private RecipientSummary?  _selectedRecipient;
    private MeteogramItem?     _highlightedItem;
    private DispatcherTimer?   _highlightTimer;
    private readonly string    _connectionString;

    // ── Forecast backing fields ───────────────────────────────────────────────

    private ForecastRun?   _selectedForecastRun;
    private int            _forecastFrameIndex;
    private int            _maxForecastFrameIndex;
    private BitmapImage?   _forecastImage;
    private string         _forecastFrameLabel = "";
    private bool           _isForecastPlaying;
    private SpeedOption    _selectedForecastSpeed;

    // ── Shared ────────────────────────────────────────────────────────────────

    private string _statusText = "Ready";

    // ── Collections ───────────────────────────────────────────────────────────

    /// <summary>Available analysis region labels, sorted alphabetically.</summary>
    public ObservableCollection<AnalysisLabel> AnalysisLabels { get; } = new();

    /// <summary>Available GFS forecast runs, newest first.</summary>
    public ObservableCollection<ForecastRun> ForecastRuns { get; } = new();

    /// <summary>Available GFS model runs for which meteograms have been rendered, newest first.</summary>
    public ObservableCollection<MeteogramRun> MeteogramRuns { get; } = new();

    /// <summary>Meteogram items for the currently selected run, sorted by ICAO.</summary>
    public ObservableCollection<MeteogramItem> MeteogramItems { get; } = new();

    /// <summary>All recipients from the database, sorted by RecipientId.</summary>
    public ObservableCollection<RecipientSummary> Recipients { get; } = new();

    /// <summary>Animation speed presets shared by both panes.</summary>
    public ObservableCollection<SpeedOption> SpeedOptions { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────────

    public RelayCommand AnalysisPlayPauseCommand   { get; }
    public RelayCommand AnalysisStepForwardCommand { get; }
    public RelayCommand AnalysisStepBackCommand    { get; }

    public RelayCommand ForecastPlayPauseCommand   { get; }
    public RelayCommand ForecastStepForwardCommand { get; }
    public RelayCommand ForecastStepBackCommand    { get; }

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when the recipient selector finds a matching meteogram to scroll to.</summary>
    public event Action<MeteogramItem>? ScrollToItem;

    /// <summary>Raised when the recipient selector finds no meteogram for the selected recipient.</summary>
    public event Action<string>? RecipientNotFound;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the ViewModel, configures both file scanner and animation timers,
    /// and performs an initial directory scan.
    /// </summary>
    /// <param name="outputDir">Path to the plots output directory.</param>
    /// <param name="connectionString">SQL Server connection string for the WeatherData database.</param>
    /// <param name="dispatcher">WPF UI dispatcher for marshalling scanner events.</param>
    public MainViewModel(string outputDir, string connectionString, Dispatcher dispatcher)
    {
        _connectionString = connectionString;
        SpeedOptions.Add(new SpeedOption("Slow",   1500));
        SpeedOptions.Add(new SpeedOption("Medium",  800));
        SpeedOptions.Add(new SpeedOption("Fast",    300));
        _selectedAnalysisSpeed = SpeedOptions[1];
        _selectedForecastSpeed = SpeedOptions[1];

        _analysisTimer        = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_selectedAnalysisSpeed.IntervalMs) };
        _analysisTimer.Tick  += OnAnalysisTimerTick;

        _forecastTimer        = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_selectedForecastSpeed.IntervalMs) };
        _forecastTimer.Tick  += OnForecastTimerTick;

        AnalysisPlayPauseCommand   = new RelayCommand(ToggleAnalysisPlay,   () => AnalysisLabels.Count > 1);
        AnalysisStepForwardCommand = new RelayCommand(StepAnalysisForward,  () => AnalysisLabels.Count > 1);
        AnalysisStepBackCommand    = new RelayCommand(StepAnalysisBack,     () => AnalysisLabels.Count > 1);

        ForecastPlayPauseCommand   = new RelayCommand(ToggleForecastPlay,   () => _selectedForecastRun?.Frames.Count > 1);
        ForecastStepForwardCommand = new RelayCommand(StepForecastForward,  () => _selectedForecastRun?.Frames.Count > 1);
        ForecastStepBackCommand    = new RelayCommand(StepForecastBack,     () => _selectedForecastRun?.Frames.Count > 1);

        _scanner = new MapFileScanner(outputDir, dispatcher);
        _scanner.DirectoryChanged += (_, _) => Refresh();

        LoadRecipients();
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
    /// resets the frame index, and updates MaxForecastFrameIndex before raising
    /// ForecastFrameIndex PropertyChanged so the Slider never sees an out-of-range value.
    /// </summary>
    public ForecastRun? SelectedForecastRun
    {
        get => _selectedForecastRun;
        set
        {
            if (_selectedForecastRun == value) return;

            _forecastTimer.Stop();
            _isForecastPlaying = false;
            OnPropertyChanged(nameof(IsForecastPlaying));

            _selectedForecastRun = value;
            OnPropertyChanged();

            MaxForecastFrameIndex = (_selectedForecastRun?.Frames.Count ?? 1) - 1;
            if (MaxForecastFrameIndex < 0) MaxForecastFrameIndex = 0;

            _forecastFrameIndex = 0;
            OnPropertyChanged(nameof(ForecastFrameIndex));
            LoadForecastImage();
        }
    }

    /// <summary>Current forecast frame index (0-based).</summary>
    public int ForecastFrameIndex
    {
        get => _forecastFrameIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, _maxForecastFrameIndex);
            if (_forecastFrameIndex == clamped) return;
            _forecastFrameIndex = clamped;
            OnPropertyChanged();
            LoadForecastImage();
        }
    }

    /// <summary>Maximum slider value for the forecast pane.</summary>
    public int MaxForecastFrameIndex
    {
        get => _maxForecastFrameIndex;
        private set { _maxForecastFrameIndex = value; OnPropertyChanged(); }
    }

    /// <summary>Decoded bitmap for the current forecast frame.</summary>
    public BitmapImage? ForecastImage
    {
        get => _forecastImage;
        private set { _forecastImage = value; OnPropertyChanged(); }
    }

    /// <summary>Human-readable label for the current forecast frame.</summary>
    public string ForecastFrameLabel
    {
        get => _forecastFrameLabel;
        private set { _forecastFrameLabel = value; OnPropertyChanged(); }
    }

    /// <summary><see langword="true"/> while the forecast animation timer is running.</summary>
    public bool IsForecastPlaying
    {
        get => _isForecastPlaying;
        private set { _isForecastPlaying = value; OnPropertyChanged(); }
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

    // ── Meteogram properties ──────────────────────────────────────────────────

    /// <summary>Currently selected meteogram run.</summary>
    public MeteogramRun? SelectedMeteogramRun
    {
        get => _selectedMeteogramRun;
        set
        {
            if (_selectedMeteogramRun == value) return;
            _selectedMeteogramRun = value;
            OnPropertyChanged();
            LoadMeteogramItems();
        }
    }

    /// <summary>
    /// Recipient selected by the user.  Finding a match scrolls to and transiently
    /// highlights that meteogram; finding no match fires <see cref="RecipientNotFound"/>.
    /// </summary>
    public RecipientSummary? SelectedRecipient
    {
        get => _selectedRecipient;
        set
        {
            if (_selectedRecipient == value) return;
            _selectedRecipient = value;
            OnPropertyChanged();
            if (value is null) return;

            var item = MeteogramItems.FirstOrDefault(m =>
                string.Equals(m.Icao,     value.FirstIcao, StringComparison.OrdinalIgnoreCase)
             && string.Equals(m.TempUnit, value.TempUnit,  StringComparison.OrdinalIgnoreCase)
             && string.Equals(m.Timezone, value.Timezone,  StringComparison.OrdinalIgnoreCase));

            if (item is not null)
            {
                ScrollToItem?.Invoke(item);
                HighlightItem(item);
            }
            else
            {
                RecipientNotFound?.Invoke(value.Name);
            }
        }
    }

    private void LoadMeteogramItems()
    {
        // Cancel any in-progress highlight — the item objects are being replaced.
        _highlightTimer?.Stop();
        if (_highlightedItem is not null)
        {
            _highlightedItem.IsHighlighted = false;
            _highlightedItem = null;
        }

        MeteogramItems.Clear();
        if (_selectedMeteogramRun is null) return;
        foreach (var item in _selectedMeteogramRun.Items)
        {
            item.Recipients = Recipients
                .Where(r => string.Equals(r.FirstIcao, item.Icao,     StringComparison.OrdinalIgnoreCase)
                         && string.Equals(r.TempUnit,  item.TempUnit, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(r.Timezone,  item.Timezone, StringComparison.OrdinalIgnoreCase))
                .ToList();
            MeteogramItems.Add(item);
        }
    }

    private void HighlightItem(MeteogramItem item)
    {
        // Clear any previous highlight.
        _highlightTimer?.Stop();
        if (_highlightedItem is not null)
            _highlightedItem.IsHighlighted = false;

        item.IsHighlighted = true;
        _highlightedItem   = item;

        _highlightTimer          = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _highlightTimer.Tick    += (_, _) =>
        {
            _highlightTimer!.Stop();
            if (_highlightedItem is not null)
            {
                _highlightedItem.IsHighlighted = false;
                _highlightedItem = null;
            }
        };
        _highlightTimer.Start();
    }

    private void LoadRecipients()
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT RecipientId, Name, Language, MetarIcao, TempUnit, Timezone " +
                "FROM Recipients " +
                "ORDER BY RecipientId";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id        = reader.GetString(0);
                var name      = reader.GetString(1);
                var lang      = reader.IsDBNull(2) ? "English" : reader.GetString(2);
                var icaoRaw   = reader.IsDBNull(3) ? null      : reader.GetString(3);
                var tempUnit  = reader.IsDBNull(4) ? "F"       : reader.GetString(4);
                var timezone  = reader.IsDBNull(5) ? "UTC"     : reader.GetString(5);
                var firstIcao = icaoRaw?.Split(',')[0].Trim();
                Recipients.Add(new RecipientSummary(id, name, lang, firstIcao, tempUnit, timezone));
            }
        }
        catch { /* DB unavailable — leave list empty, selector will be empty */ }
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
        var prevLabelName    = _selectedAnalysisLabel?.Name;
        var prevRunUtc       = _selectedForecastRun?.ModelRunUtc;
        var prevMeteogramUtc = _selectedMeteogramRun?.ModelRunUtc;

        var analysisList = _scanner.ScanAnalysis();
        AnalysisLabels.Clear();
        foreach (var l in analysisList) AnalysisLabels.Add(l);
        MaxAnalysisFrameIndex = Math.Max(0, AnalysisLabels.Count - 1);

        var forecastList = _scanner.ScanForecasts();
        ForecastRuns.Clear();
        foreach (var r in forecastList) ForecastRuns.Add(r);

        var meteogramList = _scanner.ScanMeteograms();
        MeteogramRuns.Clear();
        foreach (var r in meteogramList) MeteogramRuns.Add(r);

        SelectedAnalysisLabel = prevLabelName is not null
            ? analysisList.FirstOrDefault(l => l.Name == prevLabelName) ?? analysisList.FirstOrDefault()
            : analysisList.FirstOrDefault();

        SelectedForecastRun = prevRunUtc.HasValue
            ? forecastList.FirstOrDefault(r => r.ModelRunUtc == prevRunUtc.Value) ?? forecastList.FirstOrDefault()
            : forecastList.FirstOrDefault();

        SelectedMeteogramRun = prevMeteogramUtc.HasValue
            ? meteogramList.FirstOrDefault(r => r.ModelRunUtc == prevMeteogramUtc.Value) ?? meteogramList.FirstOrDefault()
            : meteogramList.FirstOrDefault();

        UpdateStatus();
    }

    // ── Analysis animation ────────────────────────────────────────────────────

    /// <summary>Toggles the analysis animation.  Restarts from the oldest frame if at the newest.</summary>
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
            if (_analysisFrameIndex <= 0) AnalysisFrameIndex = _maxAnalysisFrameIndex;
            _analysisTimer.Start();
            IsAnalysisPlaying = true;
        }
    }

    /// <summary>Stops animation and jumps to the newest analysis map (index 0).</summary>
    public void JumpAnalysisToNewest()
    {
        if (AnalysisLabels.Count == 0) return;
        StopAnalysisAnimation();
        AnalysisFrameIndex = 0;
    }

    /// <summary>Stops animation and jumps to the oldest analysis map (last index).</summary>
    public void JumpAnalysisToOldest()
    {
        if (AnalysisLabels.Count == 0) return;
        StopAnalysisAnimation();
        AnalysisFrameIndex = _maxAnalysisFrameIndex;
    }

    /// <summary>Stops animation and steps one analysis frame forward (newer observation).</summary>
    public void StepAnalysisForward()
    {
        if (AnalysisLabels.Count == 0) return;
        StopAnalysisAnimation();
        AnalysisFrameIndex = Math.Max(_analysisFrameIndex - 1, 0);
    }

    /// <summary>Stops animation and steps one analysis frame back (older observation).</summary>
    public void StepAnalysisBack()
    {
        if (AnalysisLabels.Count == 0) return;
        StopAnalysisAnimation();
        AnalysisFrameIndex = Math.Min(_analysisFrameIndex + 1, _maxAnalysisFrameIndex);
    }

    private void StopAnalysisAnimation()
    {
        _analysisTimer.Stop();
        IsAnalysisPlaying = false;
    }

    private void OnAnalysisTimerTick(object? sender, EventArgs e)
    {
        if (_analysisFrameIndex <= 0)
        {
            _analysisTimer.Stop();
            IsAnalysisPlaying = false;
            return;
        }
        _analysisFrameIndex--;
        _selectedAnalysisLabel = AnalysisLabels[_analysisFrameIndex];
        OnPropertyChanged(nameof(AnalysisFrameIndex));
        OnPropertyChanged(nameof(SelectedAnalysisLabel));
        LoadAnalysisImage();
    }

    // ── Forecast animation ────────────────────────────────────────────────────

    /// <summary>Toggles the forecast animation.  Restarts from frame 0 if at the last frame.</summary>
    public void ToggleForecastPlay()
    {
        if (_selectedForecastRun?.Frames.Count is null or <= 1) return;

        if (_isForecastPlaying)
        {
            _forecastTimer.Stop();
            IsForecastPlaying = false;
        }
        else
        {
            if (_forecastFrameIndex >= _maxForecastFrameIndex) ForecastFrameIndex = 0;
            _forecastTimer.Start();
            IsForecastPlaying = true;
        }
    }

    /// <summary>Stops animation and jumps to forecast frame 0 (earliest hour).</summary>
    public void JumpForecastToStart()
    {
        if (_selectedForecastRun?.Frames.Count is null or 0) return;
        StopForecastAnimation();
        ForecastFrameIndex = 0;
    }

    /// <summary>Stops animation and jumps to the final forecast hour.</summary>
    public void JumpForecastToEnd()
    {
        if (_selectedForecastRun?.Frames.Count is null or 0) return;
        StopForecastAnimation();
        ForecastFrameIndex = _maxForecastFrameIndex;
    }

    /// <summary>Stops animation and steps one forecast frame forward.</summary>
    public void StepForecastForward()
    {
        if (_selectedForecastRun?.Frames.Count is null or 0) return;
        StopForecastAnimation();
        ForecastFrameIndex = Math.Min(_forecastFrameIndex + 1, _maxForecastFrameIndex);
    }

    /// <summary>Stops animation and steps one forecast frame back.</summary>
    public void StepForecastBack()
    {
        if (_selectedForecastRun?.Frames.Count is null or 0) return;
        StopForecastAnimation();
        ForecastFrameIndex = Math.Max(_forecastFrameIndex - 1, 0);
    }

    private void StopForecastAnimation()
    {
        _forecastTimer.Stop();
        IsForecastPlaying = false;
    }

    private void OnForecastTimerTick(object? sender, EventArgs e)
    {
        if (_forecastFrameIndex >= _maxForecastFrameIndex)
        {
            _forecastTimer.Stop();
            IsForecastPlaying = false;
            return;
        }
        _forecastFrameIndex++;
        OnPropertyChanged(nameof(ForecastFrameIndex));
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
        ForecastImage      = LoadBitmap(frame?.FilePath);
        ForecastFrameLabel = frame?.HourLabel ?? "";
        UpdateStatus();
    }

    private ForecastFrame? CurrentForecastFrame =>
        _selectedForecastRun?.Frames.Count > 0
            ? _selectedForecastRun.Frames[Math.Clamp(_forecastFrameIndex, 0, _selectedForecastRun.Frames.Count - 1)]
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
        else if (_selectedForecastRun is not null)
            parts.Add($"Run: {_selectedForecastRun.Label}");

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
        _highlightTimer?.Stop();
        _scanner.Dispose();
    }
}
