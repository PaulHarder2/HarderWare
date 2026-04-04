using System.Windows;

namespace WxManager;

/// <summary>
/// Main application window. Hosts a <see cref="TabControl"/> whose first tab
/// is the <see cref="RecipientsTab"/> user control.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Initializes a new instance of <see cref="MainWindow"/>.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
