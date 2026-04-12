using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WxServices.Common;

namespace WxViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        VersionRun.Text = $"  {WxPaths.ProductVersion}";
        _vm = viewModel;
        DataContext = _vm;
        _vm.ScrollToItem      += OnScrollToItem;
        _vm.RecipientNotFound += OnRecipientNotFound;
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
