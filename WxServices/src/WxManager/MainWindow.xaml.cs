using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

using WxServices.Common;

namespace WxManager;

/// <summary>
/// Main application window. Hosts a <see cref="TabControl"/> whose first tab
/// is the <see cref="RecipientsTab"/> user control.
/// Uses <see cref="System.Windows.Shell.WindowChrome"/> with a custom title bar
/// so the window chrome matches the dark HarderWare theme.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Initializes a new instance of <see cref="MainWindow"/>.</summary>
    public MainWindow()
    {
        InitializeComponent();
        VersionRun.Text = $"  {WxPaths.ProductVersion}";
        SetupTab.AllChecksPassed += OnAllChecksPassed;
        ConfigureTab.ConfigurationSaved += OnConfigurationSaved;
    }

    // A WindowStyle=None + WindowChrome window maximizes over the taskbar unless WM_GETMINMAXINFO is
    // handled to clamp the maximized bounds to the monitor work area. Without this the bottom of every
    // maximized view sits behind the taskbar (surfaced by WX-219's bottom-anchored Report Findings).
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaximizedToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ClampMaximizedToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var work = info.rcWork;
        var mon = info.rcMonitor;
        mmi.ptMaxPosition.x = work.left - mon.left;
        mmi.ptMaxPosition.y = work.top - mon.top;
        mmi.ptMaxSize.x = work.right - work.left;
        mmi.ptMaxSize.y = work.bottom - work.top;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private void OnAllChecksPassed()
    {
        ConfigureTabItem.IsEnabled = true;
        RecipientsTabItem.IsEnabled = true;
        LocalitiesTabItem.IsEnabled = true;
        LanguagesTabItem.IsEnabled = true;
        TranslationQaTabItem.IsEnabled = true;
        AnnouncementTabItem.IsEnabled = true;
    }

    private async void OnConfigurationSaved()
    {
        // Re-run prerequisite checks after saving configuration,
        // in case the user changed paths or connection strings.
        RecipientsTabItem.IsEnabled = false;
        LocalitiesTabItem.IsEnabled = false;
        LanguagesTabItem.IsEnabled = false;
        TranslationQaTabItem.IsEnabled = false;
        AnnouncementTabItem.IsEnabled = false;
        ConfigureTabItem.IsEnabled = false;
        MainTabs.SelectedIndex = 0; // switch to Setup tab
        await SetupTab.RecheckAsync();
    }

    /// <summary>
    /// Handles mouse-down on the custom title bar: drags the window on single click,
    /// or toggles maximize/restore on double-click.
    /// </summary>
    /// <param name="sender">The title-bar Border.</param>
    /// <param name="e">Mouse button event arguments.</param>
    /// <sideeffects>Calls <see cref="DragMove"/> or <see cref="ToggleMaximize"/>.</sideeffects>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            ToggleMaximize();
        else
            DragMove();
    }

    /// <summary>Minimizes the window.</summary>
    /// <param name="sender">The Minimize button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Sets <see cref="Window.WindowState"/> to <see cref="WindowState.Minimized"/>.</sideeffects>
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>Toggles the window between maximized and normal state.</summary>
    /// <param name="sender">The Maximize/Restore button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Calls <see cref="ToggleMaximize"/>.</sideeffects>
    private void MaxRestoreButton_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    /// <summary>Closes the window.</summary>
    /// <param name="sender">The Close button.</param>
    /// <param name="e">Event arguments (unused).</param>
    /// <sideeffects>Calls <see cref="Window.Close"/>.</sideeffects>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>
    /// Toggles <see cref="Window.WindowState"/> between
    /// <see cref="WindowState.Maximized"/> and <see cref="WindowState.Normal"/>.
    /// </summary>
    /// <sideeffects>Sets <see cref="Window.WindowState"/>.</sideeffects>
    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
}