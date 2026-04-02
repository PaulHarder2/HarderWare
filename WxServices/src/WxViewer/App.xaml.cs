using System.IO;
using System.Text.Json;
using System.Windows;

namespace WxViewer;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var outputDir = ReadOutputDir();
        _viewModel = new MainViewModel(outputDir, Dispatcher);

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private static string ReadOutputDir()
    {
        const string defaultDir = @"C:\HarderWare\plots";
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(settingsPath)) return defaultDir;

        try
        {
            using var stream = File.OpenRead(settingsPath);
            var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("OutputDir", out var prop))
                return prop.GetString() ?? defaultDir;
        }
        catch { /* fall through */ }

        return defaultDir;
    }
}
