using System.Windows;
using System.Windows.Input;

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
        SetupTab.AllChecksPassed += OnAllChecksPassed;
    }

    private void OnAllChecksPassed()
    {
        RecipientsTabItem.IsEnabled = true;
        AnnouncementTabItem.IsEnabled = true;
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
