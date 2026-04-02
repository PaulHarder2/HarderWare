using System.Windows;
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Space:
                _vm.TogglePlay();
                e.Handled = true;
                break;

            case Key.Right:
                _vm.StepForward();
                e.Handled = true;
                break;

            case Key.Left:
                _vm.StepBack();
                e.Handled = true;
                break;
        }
    }
}
