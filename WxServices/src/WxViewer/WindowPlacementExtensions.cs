using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

using WxServices.Common;
using WxServices.Logging;

namespace WxServices.Wpf;

/// <summary>
/// WPF glue for <see cref="WindowPlacement"/> (WX-291): restores a window's last-saved
/// position/size on launch and persists it on close, always fitting the window onto a real
/// monitor and never larger than it.
/// </summary>
/// <remarks>
/// This file is compiled into both <c>WxViewer</c> and <c>WxManager</c> (linked from
/// <c>WxManager.csproj</c>), so the two apps share one implementation. The pure placement math
/// lives in <see cref="WindowPlacement"/>; this class only resolves the target monitor's work
/// area (Win32 <c>MonitorFromRect</c>/<c>GetMonitorInfo</c>, the same pattern WxManager already
/// uses for its maximize clamp) and applies the result to a live <see cref="Window"/>.
/// </remarks>
public static class WindowPlacementExtensions
{
    /// <summary>
    /// Wires <paramref name="window"/> to restore its saved placement on show and save it on close.
    /// Call once from the window constructor, before <see cref="Window.Show"/>. On first run — or
    /// when the saved rectangle is off-screen or larger than the current monitor — the window opens
    /// at the 1920x1080 default (or a shrunk-to-fit variant), centered and fully visible.
    /// </summary>
    /// <param name="window">The top-level window to persist.</param>
    /// <param name="appName">Per-app key for the placement file (e.g. <c>"wxviewer"</c>, <c>"wxmanager"</c>).</param>
    public static void RestoreAndPersistPlacement(this Window window, string appName)
    {
        var saved = WindowPlacement.Load(appName);

        // Track the last non-minimized state so that closing from a minimized window still
        // records whether it was maximized (window.WindowState is Minimized on that path).
        var lastNonMinimized = WindowState.Normal;

        // Apply at SourceInitialized — the window has an HWND (so we know its DPI and can resolve
        // its monitor) but has not yet been rendered, so there is no visible reposition flicker.
        window.SourceInitialized += (_, _) => ApplyPlacement(window, saved);
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState != WindowState.Minimized)
                lastNonMinimized = window.WindowState;
        };
        window.Closing += (_, _) => Capture(window, lastNonMinimized).Save(appName);
    }

    private static void ApplyPlacement(Window window, WindowPlacement? saved)
    {
        double waLeft, waTop, waWidth, waHeight;

        var hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is not null)
        {
            // Single device scale (these apps are System-DPI aware, so one scale governs the desktop).
            var toDevice = source.CompositionTarget.TransformToDevice;
            var sx = toDevice.M11 > 0 ? toDevice.M11 : 1.0;
            var sy = toDevice.M22 > 0 ? toDevice.M22 : 1.0;

            // Pick the monitor nearest the saved rectangle (so a rect that now lies off every monitor
            // still resolves to the closest connected one), or the primary monitor on first run.
            var monitor = saved is not null
                ? MonitorFromRect(ToPhysical(saved, sx, sy), MONITOR_DEFAULTTONEAREST)
                : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);

            if (!TryGetWorkAreaDip(monitor, sx, sy, out waLeft, out waTop, out waWidth, out waHeight))
            {
                // Win32 lookup failed — fall back to WPF's primary work area, already in DIPs. The
                // window still opens correctly on the primary; log so a multi-monitor mis-placement
                // is diagnosable rather than silent.
                Logger.Warn("WindowPlacement: monitor work-area lookup failed; falling back to primary work area.");
                var wa = SystemParameters.WorkArea;
                (waLeft, waTop, waWidth, waHeight) = (wa.Left, wa.Top, wa.Width, wa.Height);
            }
        }
        else
        {
            // Should not happen at SourceInitialized (the HWND exists), so we can't resolve the
            // window's own monitor/DPI here. Still fit and apply against WPF's primary work area
            // (DIPs) rather than bailing to the raw XAML size/position — a safe, visible fallback.
            Logger.Warn("WindowPlacement: no composition target at SourceInitialized; using primary work area.");
            var wa = SystemParameters.WorkArea;
            (waLeft, waTop, waWidth, waHeight) = (wa.Left, wa.Top, wa.Width, wa.Height);
        }

        var basis = saved ?? WindowPlacement.CenteredDefault(waLeft, waTop, waWidth, waHeight);
        var fitted = basis.ClampToWorkArea(waLeft, waTop, waWidth, waHeight);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = fitted.Left;
        window.Top = fitted.Top;
        window.Width = fitted.Width;
        window.Height = fitted.Height;
        window.WindowState = (saved?.Maximized ?? false) ? WindowState.Maximized : WindowState.Normal;
    }

    private static WindowPlacement Capture(Window window, WindowState lastNonMinimized)
    {
        // If the window is minimized at close, its live WindowState/Left/Top are not meaningful;
        // use the last non-minimized state so a maximized-then-minimized window still reopens
        // maximized. RestoreBounds carries the normal-state rectangle even while maximized or
        // minimized, so it is the right geometry source in every non-Normal case.
        var effectiveState = window.WindowState == WindowState.Minimized ? lastNonMinimized : window.WindowState;

        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;

        return new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = effectiveState == WindowState.Maximized,
        };
    }

    /// <summary>
    /// Returns the work area (device-independent pixels) of the monitor currently containing
    /// <paramref name="window"/>. Use this to clamp a frameless maximized window to the *right*
    /// monitor: <see cref="SystemParameters.WorkArea"/> reports only the primary monitor, so a
    /// maximize on a secondary display would otherwise be pushed to the primary's bounds (WX-291).
    /// Falls back to <see cref="SystemParameters.WorkArea"/> if the window has no HWND yet or the
    /// Win32 lookup fails.
    /// </summary>
    public static Rect CurrentMonitorWorkArea(this Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var source = hwnd != IntPtr.Zero ? HwndSource.FromHwnd(hwnd) : null;
        if (source?.CompositionTarget is not null)
        {
            var toDevice = source.CompositionTarget.TransformToDevice;
            var sx = toDevice.M11 > 0 ? toDevice.M11 : 1.0;
            var sy = toDevice.M22 > 0 ? toDevice.M22 : 1.0;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (TryGetWorkAreaDip(monitor, sx, sy, out var l, out var t, out var w, out var h))
                return new Rect(l, t, w, h);
        }
        return SystemParameters.WorkArea;
    }

    private static bool TryGetWorkAreaDip(
        IntPtr monitor, double sx, double sy,
        out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0;
        if (monitor == IntPtr.Zero)
            return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        var work = info.rcWork; // physical pixels
        left = work.left / sx;
        top = work.top / sy;
        width = (work.right - work.left) / sx;
        height = (work.bottom - work.top) / sy;
        return width > 0 && height > 0;
    }

    private static RECT ToPhysical(WindowPlacement p, double sx, double sy) => new()
    {
        left = (int)Math.Round(p.Left * sx),
        top = (int)Math.Round(p.Top * sy),
        right = (int)Math.Round((p.Left + p.Width) * sx),
        bottom = (int)Math.Round((p.Top + p.Height) * sy),
    };

    private const int MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(in RECT lprc, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}