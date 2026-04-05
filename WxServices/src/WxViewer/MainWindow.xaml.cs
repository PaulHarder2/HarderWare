using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace WxViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = _vm;
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
    /// Handles arrow-key navigation for both panes.
    /// Left/Right step the forecast pane; Up/Down step the observation pane.
    /// Keys are ignored when a Slider or ComboBox has keyboard focus so those
    /// controls continue to respond to arrow keys normally.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Keyboard.FocusedElement is Slider or ComboBox or Selector)
            return;

        switch (e.Key)
        {
            case Key.Right: _vm.StepForecastForward();  e.Handled = true; break;
            case Key.Left:  _vm.StepForecastBack();     e.Handled = true; break;
            case Key.Up:    _vm.StepAnalysisForward();  e.Handled = true; break;
            case Key.Down:  _vm.StepAnalysisBack();     e.Handled = true; break;
        }
    }
}
