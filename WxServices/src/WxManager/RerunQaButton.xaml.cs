using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

using MetarParser.Data.Entities;

namespace WxManager;

/// <summary>
/// WX-235 — the shared "Rerun QA" button used on both the Translation-QA and Vocabulary tabs. It binds to a
/// language (<see cref="Iso"/>) and to <c>App.QaRerunCoordinator</c>, rendering that language's state: idle
/// ("Rerun QA"), in-progress (disabled, with right-pointing chevrons sweeping continuously for the whole run),
/// a transient "✓ Updated HH:mm" on a fresh completion, or a persistent "⚠ Rerun failed" (reason in the
/// tooltip) until rerun. The visual is a pure function of the coordinator's per-language status, decoupled
/// from the poll cadence: a Running→Running poll is a no-op, so the sweep never restarts mid-run.
/// Judge-agnostic — it triggers "a regeneration", and never names a judge.
/// </summary>
public partial class RerunQaButton : UserControl
{
    /// <summary>The ISO 639-1 code of the language this button acts on (set by the host tab to its current selection).</summary>
    public static readonly DependencyProperty IsoProperty =
        DependencyProperty.Register(nameof(Iso), typeof(string), typeof(RerunQaButton),
            new PropertyMetadata("", OnIsoChanged));

    public string Iso
    {
        get => (string)GetValue(IsoProperty);
        set => SetValue(IsoProperty, value);
    }

    private readonly Storyboard _sweep;
    private QaRerunStatus? _lastStatus;
    private DispatcherTimer? _doneTimer;

    public RerunQaButton()
    {
        InitializeComponent();
        _sweep = (Storyboard)Resources["Sweep"];
        BuildChevrons();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnIsoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (RerunQaButton)d;
        c._lastStatus = null;   // switching languages must never carry a stale ✓-flash trigger across
        if (c.IsLoaded)
            c.Render();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.QaRerunCoordinator.StatusChanged += OnCoordinatorChanged;
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.QaRerunCoordinator.StatusChanged -= OnCoordinatorChanged;
        CancelDoneTimer();
        StopSweep();
    }

    private void OnCoordinatorChanged(string iso)
    {
        if (string.Equals(iso, Iso, StringComparison.Ordinal))
            Render();
    }

    private async void OnClick(object sender, RoutedEventArgs e)
    {
        var iso = Iso;
        if (string.IsNullOrEmpty(iso))
            return;
        try
        {
            await App.QaRerunCoordinator.RequestRerunAsync(iso);
        }
        catch
        {
            // A failed request write is reflected by the next poll; don't crash the UI thread.
        }
    }

    private void Render()
    {
        var v = string.IsNullOrEmpty(Iso) ? QaRerunView.None : App.QaRerunCoordinator.StatusFor(Iso);
        switch (v.Status)
        {
            case QaRerunStatus.Running:
                ShowRunning();
                break;
            case QaRerunStatus.Failed:
                ShowFailed(v.Error);
                break;
            case QaRerunStatus.Succeeded when _lastStatus == QaRerunStatus.Running:
                ShowDoneTransient(v.CompletedAtUtc);   // ✓ only on a LIVE completion we were watching
                break;
            default:
                ShowIdle();
                break;
        }
        _lastStatus = v.Status;
    }

    private void ShowRunning()
    {
        CancelDoneTimer();
        Label.Text = "running…";
        Btn.IsEnabled = false;
        Btn.ToolTip = null;
        ChevronHost.Visibility = Visibility.Visible;
        StartSweep();
    }

    private void ShowIdle()
    {
        CancelDoneTimer();
        StopSweep();
        ChevronHost.Visibility = Visibility.Collapsed;
        Label.Text = "Rerun QA";
        Btn.IsEnabled = true;
        Btn.ToolTip = null;
    }

    private void ShowFailed(string? error)
    {
        CancelDoneTimer();
        StopSweep();
        ChevronHost.Visibility = Visibility.Collapsed;
        Label.Text = "⚠ Rerun failed";
        Btn.IsEnabled = true;   // re-pressable — pressing requeues a fresh run
        Btn.ToolTip = string.IsNullOrWhiteSpace(error) ? "The regeneration failed." : error;
    }

    private void ShowDoneTransient(DateTime? completedUtc)
    {
        StopSweep();
        ChevronHost.Visibility = Visibility.Collapsed;
        var local = (completedUtc ?? DateTime.UtcNow).ToLocalTime();
        Label.Text = "✓ Updated " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        Btn.IsEnabled = true;
        Btn.ToolTip = null;
        CancelDoneTimer();
        _doneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _doneTimer.Tick += (_, _) =>
        {
            CancelDoneTimer();
            // Revert to idle only if it's still the same terminal-success state (no newer request landed).
            if (App.QaRerunCoordinator.StatusFor(Iso).Status == QaRerunStatus.Succeeded)
                ShowIdle();
        };
        _doneTimer.Start();
    }

    private void CancelDoneTimer()
    {
        if (_doneTimer is null)
            return;
        _doneTimer.Stop();
        _doneTimer = null;
    }

    private void StartSweep() => _sweep.Begin(this, isControllable: true);

    private void StopSweep() => _sweep.Stop(this);

    private void BuildChevrons()
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x5a, 0xa0, 0xe0));
        accent.Freeze();
        // Enough 16px chevrons to over-fill the button width; the strip starts one tile left and shifts +16.
        for (var i = 0; i < 22; i++)
        {
            ChevronStrip.Children.Add(new Path
            {
                Data = Geometry.Parse("M3,4 L9,10 L3,16"),
                Stroke = accent,
                StrokeThickness = 3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 16,
                Height = 20,
                Stretch = Stretch.None,
            });
        }
    }
}