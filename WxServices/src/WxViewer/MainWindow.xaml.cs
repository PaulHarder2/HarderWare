using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(KeypadZoomHook);
    }

    // Hooks WM_KEYDOWN so numpad 8 (zoom in) and numpad 2 (zoom out) work
    // regardless of NumLock state. When NumLock is off those keys arrive as
    // VK_UP/VK_DOWN, but only the dedicated arrow block sets the extended-key
    // bit in lParam — the numpad does not — so the scan-code flag tells them
    // apart and leaves the real arrow keys free for normal navigation.
    private IntPtr KeypadZoomHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const int VK_UP      = 0x26;
        const int VK_DOWN    = 0x28;
        const int VK_NUMPAD8 = 0x68;
        const int VK_NUMPAD2 = 0x62;

        if (msg != WM_KEYDOWN) return IntPtr.Zero;

        int  vk       = wParam.ToInt32();
        bool extended = ((lParam.ToInt64() >> 24) & 0x01) != 0;

        bool zoomIn  = vk == VK_NUMPAD8 || (vk == VK_UP   && !extended);
        bool zoomOut = vk == VK_NUMPAD2 || (vk == VK_DOWN && !extended);
        if (!zoomIn && !zoomOut) return IntPtr.Zero;

        Border? viewport = AnalysisViewport.IsMouseOver ? AnalysisViewport
                         : ForecastViewport.IsMouseOver ? ForecastViewport
                         : null;
        if (viewport is null) return IntPtr.Zero;

        BeginFastScaling();
        var (scale, translate, isAnalysis) = GetTransforms(viewport);
        var mousePos = Mouse.GetPosition(viewport);
        double factor = zoomIn ? 1.15 : 1.0 / 1.15;
        ApplyZoom(scale, translate, viewport, mousePos, factor, isAnalysis);

        if (LinkPanesToggle.IsChecked == true)
        {
            var (ls, lt, li) = GetLinkedTransforms(isAnalysis);
            var linkedViewport = li ? (Border)AnalysisViewport : (Border)ForecastViewport;
            ApplyZoom(ls, lt, linkedViewport, mousePos, factor, li);
        }

        handled = true;
        return IntPtr.Zero;
    }

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

        // Check zoom-level swap thresholds. Pass the zoom factor so the check
        // can be direction-aware (zoom-in steps consider swap-up only,
        // zoom-out steps swap-down only — prevents ping-pong).
        CheckZoomThreshold(scale, translate, viewport, isAnalysis, factor);
    }

    private void CheckZoomThreshold(ScaleTransform scale, TranslateTransform translate,
                                    Border viewport, bool isAnalysis, double factor)
    {
        int currentZoom = isAnalysis ? _vm.AnalysisCurrentZoom : _vm.ForecastCurrentZoom;

        // Each Z-level has 2x the pixel density of the previous, so the
        // "stretched enough to look blurry" scale doubles per level. Up: Z1→Z2
        // at 3.0, Z2→Z3 at 6.0. Down thresholds equal the previous level's up
        // threshold; because the checks are direction-aware there is no
        // ping-pong even though the up and down thresholds coincide.
        double levelFactor = 1 << (currentZoom - 1);  // 1, 2, 4, ...
        double upThreshold   = 3.0 * levelFactor;
        double downThreshold = 3.0 * levelFactor / 2.0;  // = up threshold of level below

        if (factor > 1.0 && scale.ScaleX >= upThreshold)
        {
            int actual = isAnalysis
                ? _vm.SwapAnalysisZoomLevel(currentZoom + 1)
                : _vm.SwapForecastZoomLevel(currentZoom + 1);

            if (actual > currentZoom)
                CrossfadeZoomSwap(isAnalysis);
        }
        else if (factor < 1.0 && currentZoom > 1 && scale.ScaleX < downThreshold)
        {
            int actual = isAnalysis
                ? _vm.SwapAnalysisZoomLevel(currentZoom - 1)
                : _vm.SwapForecastZoomLevel(currentZoom - 1);

            if (actual < currentZoom)
                CrossfadeZoomSwap(isAnalysis);
        }
    }

    // The Image controls use Stretch="Uniform", so a bitmap of any Z-level fills
    // the viewport identically at ScaleX=1. Swapping the source therefore
    // requires no scale/translate change — the view stays put and the point
    // under the cursor does not move.
    private void CrossfadeZoomSwap(bool isAnalysis)
    {
        var image = isAnalysis ? (UIElement)AnalysisMapImage : (UIElement)ForecastMapImage;
        var fade = new DoubleAnimation(0.1, 1.0, TimeSpan.FromMilliseconds(1000))
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

    protected override void OnClosed(EventArgs e)
    {
        _qualityTimer.Stop();
        _vm.ScrollToItem      -= OnScrollToItem;
        _vm.RecipientNotFound -= OnRecipientNotFound;
        _vm.ZoomResetRequested -= OnZoomResetRequested;
        base.OnClosed(e);
    }

    private void ComboBox_DropDownClosed(object sender, EventArgs e)
        => Keyboard.Focus(this);

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
