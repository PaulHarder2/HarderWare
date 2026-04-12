using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WxServices.Common;

namespace WxViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _qualityTimer;

    private Point  _panStart;
    private double _panStartTx, _panStartTy;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        VersionRun.Text = $"  {WxPaths.ProductVersion}";
        _vm = viewModel;
        DataContext = _vm;
        _vm.ScrollToItem      += OnScrollToItem;
        _vm.RecipientNotFound += OnRecipientNotFound;
        _vm.ZoomResetRequested += OnZoomResetRequested;
        StateChanged          += OnStateChanged;

        // Timer to restore high-quality scaling after zoom/pan interaction stops.
        _qualityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _qualityTimer.Tick += (_, _) =>
        {
            _qualityTimer.Stop();
            RenderOptions.SetBitmapScalingMode(AnalysisMapImage, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(ForecastMapImage, BitmapScalingMode.HighQuality);
        };
    }

    /// <summary>
    /// Constrains the maximized window to the working area so it doesn't
    /// overlap the taskbar.  Required because WindowStyle="None" bypasses
    /// the shell's default taskbar-avoidance logic.
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            var wa = SystemParameters.WorkArea;
            MaxHeight = wa.Height;
            MaxWidth  = wa.Width;
            Left      = wa.Left;
            Top       = wa.Top;
        }
        else
        {
            MaxHeight = double.PositiveInfinity;
            MaxWidth  = double.PositiveInfinity;
        }
    }

    private void OnScrollToItem(MeteogramItem item)
    {
        var index = _vm.MeteogramItems.IndexOf(item);
        if (index < 0) return;
        if (MeteogramItemsControl.ItemContainerGenerator.ContainerFromIndex(index)
                is FrameworkElement container)
            container.BringIntoView();
    }

    private void OnRecipientNotFound(string name)
        => MessageBox.Show($"No meteogram found for {name}.",
                           "Meteogram Not Found",
                           MessageBoxButton.OK,
                           MessageBoxImage.Information);

    private void RecipientsButton_Click(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.Button)sender).DataContext is MeteogramItem item)
            new RecipientsDialog(item) { Owner = this }.ShowDialog();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// Hides the ToolBar overflow toggle button (the chevron blob at the right end).
    /// The gripper (dots at the left end) is already suppressed via
    /// <c>ToolBarTray.IsLocked="True"</c> in the XAML style.
    /// </summary>
    private void ToolBar_Loaded(object sender, RoutedEventArgs e)
    {
        var tb = (ToolBar)sender;
        if (tb.Template.FindName("OverflowGrid", tb) is FrameworkElement overflow)
            overflow.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Prevents the inner horizontal ScrollViewer on each meteogram row from
    /// swallowing vertical mouse wheel events.  Marks the tunnelling event
    /// handled, then re-raises a bubbling MouseWheel event on the inner
    /// ScrollViewer's parent so the outer vertical ScrollViewer receives it.
    /// </summary>
    private void MeteogramImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
        };
        ((UIElement)((ScrollViewer)sender).Parent).RaiseEvent(args);
    }

    // ── Zoom/Pan handlers ──────────────────────────────────────────────────────

    /// <summary>
    /// Switches both map images to fast low-quality scaling and restarts the
    /// timer that will restore high-quality scaling after interaction stops.
    /// </summary>
    private void BeginFastScaling()
    {
        RenderOptions.SetBitmapScalingMode(AnalysisMapImage, BitmapScalingMode.LowQuality);
        RenderOptions.SetBitmapScalingMode(ForecastMapImage, BitmapScalingMode.LowQuality);
        _qualityTimer.Stop();
        _qualityTimer.Start();
    }

    /// <summary>Resolves which pane's transforms to use based on the sender Border.</summary>
    private (ScaleTransform scale, TranslateTransform translate, bool isAnalysis) GetTransforms(object sender)
    {
        if (sender == AnalysisViewport)
            return (AnalysisScale, AnalysisTranslate, true);
        return (ForecastScale, ForecastTranslate, false);
    }

    /// <summary>Returns the paired pane's transforms (for linked pane mode).</summary>
    private (ScaleTransform scale, TranslateTransform translate, bool isAnalysis) GetLinkedTransforms(bool sourceIsAnalysis)
        => sourceIsAnalysis
            ? (ForecastScale, ForecastTranslate, false)
            : (AnalysisScale, AnalysisTranslate, true);

    private void MapViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        BeginFastScaling();
        var (scale, translate, isAnalysis) = GetTransforms(sender);
        var viewport = (Border)sender;
        var mousePos = e.GetPosition(viewport);
        double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;

        ApplyZoom(scale, translate, viewport, mousePos, factor, isAnalysis);

        // If Link Panes is on, mirror to the other pane.
        if (LinkPanesToggle.IsChecked == true)
        {
            var (ls, lt, li) = GetLinkedTransforms(isAnalysis);
            var linkedViewport = li ? (Border)AnalysisViewport : (Border)ForecastViewport;
            ApplyZoom(ls, lt, linkedViewport, mousePos, factor, li);
        }

        e.Handled = true;
    }

    private void ApplyZoom(ScaleTransform scale, TranslateTransform translate,
                           Border viewport, Point mousePos, double factor, bool isAnalysis)
    {
        int currentZoom = isAnalysis ? _vm.AnalysisCurrentZoom : _vm.ForecastCurrentZoom;
        double oldScale = scale.ScaleX;
        double newScale = oldScale * factor;

        // Don't shrink below fit-to-window.
        if (newScale < 1.0) newScale = 1.0;

        // Zoom toward cursor: adjust translation so the point under the cursor stays put.
        double rx = (mousePos.X - translate.X) / oldScale;
        double ry = (mousePos.Y - translate.Y) / oldScale;

        scale.ScaleX = newScale;
        scale.ScaleY = newScale;
        translate.X  = mousePos.X - rx * newScale;
        translate.Y  = mousePos.Y - ry * newScale;

        ClampTranslation(scale, translate, viewport);

        // Check zoom-level swap thresholds.
        CheckZoomThreshold(scale, translate, viewport, isAnalysis);
    }

    private void CheckZoomThreshold(ScaleTransform scale, TranslateTransform translate,
                                    Border viewport, bool isAnalysis)
    {
        int currentZoom = isAnalysis ? _vm.AnalysisCurrentZoom : _vm.ForecastCurrentZoom;

        // Swap UP when zoomed in enough that the z1 pixels are getting blurry.
        // At 3.0x the viewport shows ~1/9 of the image area — the user has had
        // time to explore at the current level before the sharper image loads.
        if (scale.ScaleX >= 3.0)
        {
            int actual = isAnalysis
                ? _vm.SwapAnalysisZoomLevel(currentZoom + 1)
                : _vm.SwapForecastZoomLevel(currentZoom + 1);

            if (actual > currentZoom)
            {
                // Higher-res image loaded — the new image has 2x the pixels per
                // axis, so halve the visual scale to maintain the same viewport.
                CrossfadeZoomSwap(scale, translate, 0.5, isAnalysis);
            }
        }
        // Swap DOWN when zoomed back out to roughly where we entered this level.
        // Zoom-in swaps at 3.0x and halves to 1.5x, so swap out at 1.4x.
        else if (scale.ScaleX <= 1.4 && currentZoom > 1)
        {
            int actual = isAnalysis
                ? _vm.SwapAnalysisZoomLevel(currentZoom - 1)
                : _vm.SwapForecastZoomLevel(currentZoom - 1);

            if (actual < currentZoom)
            {
                CrossfadeZoomSwap(scale, translate, 2.0, isAnalysis);
            }
        }
    }

    /// <summary>
    /// Adjusts scale/translate for the zoom level swap and applies a brief
    /// opacity crossfade to smooth the image transition.
    /// </summary>
    private void CrossfadeZoomSwap(ScaleTransform scale, TranslateTransform translate,
                                   double scaleFactor, bool isAnalysis)
    {
        scale.ScaleX *= scaleFactor;
        scale.ScaleY *= scaleFactor;
        translate.X  *= scaleFactor;
        translate.Y  *= scaleFactor;

        // Brief crossfade on the image element.
        var image = isAnalysis ? (UIElement)AnalysisMapImage : (UIElement)ForecastMapImage;
        var fade = new DoubleAnimation(0.5, 1.0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        image.BeginAnimation(OpacityProperty, fade);
    }

    private void MapViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-click resets zoom.
        if (e.ClickCount == 2)
        {
            var (_, _, isAnalysis) = GetTransforms(sender);
            if (isAnalysis)
                _vm.ResetAnalysisZoom();
            else
                _vm.ResetForecastZoom();
            e.Handled = true;
            return;
        }

        var viewport = (Border)sender;
        _panStart   = e.GetPosition(viewport);
        var (_, translate, _) = GetTransforms(sender);
        _panStartTx = translate.X;
        _panStartTy = translate.Y;
        viewport.CaptureMouse();
        e.Handled = true;
    }

    private void MapViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var viewport = (Border)sender;
        viewport.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MapViewport_MouseMove(object sender, MouseEventArgs e)
    {
        var viewport = (Border)sender;
        if (!viewport.IsMouseCaptured) return;
        BeginFastScaling();

        var pos   = e.GetPosition(viewport);
        double dx = pos.X - _panStart.X;
        double dy = pos.Y - _panStart.Y;

        var (scale, translate, isAnalysis) = GetTransforms(sender);
        translate.X = _panStartTx + dx;
        translate.Y = _panStartTy + dy;
        ClampTranslation(scale, translate, viewport);

        // Mirror pan to linked pane if enabled.
        if (LinkPanesToggle.IsChecked == true)
        {
            var (ls, lt, _) = GetLinkedTransforms(isAnalysis);
            var linkedViewport = isAnalysis ? (Border)ForecastViewport : (Border)AnalysisViewport;
            lt.X = _panStartTx + dx;
            lt.Y = _panStartTy + dy;
            ClampTranslation(ls, lt, linkedViewport);
        }

        e.Handled = true;
    }

    private bool _inZoomReset;
    private void OnZoomResetRequested(string pane)
    {
        if (_inZoomReset) return;
        _inZoomReset = true;
        try
        {
            // Always reset the requested pane's transforms.
            AnalysisScale.ScaleX = 1; AnalysisScale.ScaleY = 1;
            AnalysisTranslate.X = 0;  AnalysisTranslate.Y = 0;
            ForecastScale.ScaleX = 1; ForecastScale.ScaleY = 1;
            ForecastTranslate.X = 0;  ForecastTranslate.Y = 0;

            // If linked, reset the other pane's zoom level too.
            if (LinkPanesToggle.IsChecked == true)
            {
                if (pane == "Analysis") _vm.ResetForecastZoom();
                else                    _vm.ResetAnalysisZoom();
            }
        }
        finally { _inZoomReset = false; }
    }

    /// <summary>
    /// Clamps translation so the image edges cannot pull away from the viewport edges.
    /// When the image is at scale 1 (fit-to-window), translation is forced to 0,0.
    /// </summary>
    private static void ClampTranslation(ScaleTransform scale, TranslateTransform translate, Border viewport)
    {
        double vw = viewport.ActualWidth;
        double vh = viewport.ActualHeight;
        double iw = vw * scale.ScaleX;
        double ih = vh * scale.ScaleY;

        // If image is smaller than or equal to viewport, centre it.
        if (iw <= vw)
            translate.X = 0;
        else
            translate.X = Math.Clamp(translate.X, vw - iw, 0);

        if (ih <= vh)
            translate.Y = 0;
        else
            translate.Y = Math.Clamp(translate.Y, vh - ih, 0);
    }

    /// <summary>
    /// Handles arrow-key navigation for both panes.
    /// Left/Right step the forecast pane; Up/Down step the observation pane.
    /// Ctrl+Left/Right jump to the first/last forecast hour.
    /// Ctrl+Up/Down jump to the newest/oldest analysis map.
    /// Keys are ignored when a Slider or ComboBox has keyboard focus so those
    /// controls continue to respond to arrow keys normally.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Keyboard.FocusedElement is Slider or ComboBox or Selector)
            return;

        bool ctrl = (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Right: if (ctrl) _vm.JumpForecastToEnd();     else _vm.StepForecastForward(); e.Handled = true; break;
            case Key.Left:  if (ctrl) _vm.JumpForecastToStart();   else _vm.StepForecastBack();    e.Handled = true; break;
            case Key.Up:    if (ctrl) _vm.JumpAnalysisToNewest();  else _vm.StepAnalysisForward(); e.Handled = true; break;
            case Key.Down:  if (ctrl) _vm.JumpAnalysisToOldest();  else _vm.StepAnalysisBack();    e.Handled = true; break;
        }
    }
}
