using System.IO;
using System.Text.Json;
using System.Windows;
using WxServices.Common;

namespace WxViewer;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths            = new WxPaths(ReadInstallRoot());
        var outputDir        = paths.PlotsDir;
        var connectionString = ReadConnectionString();
        _viewModel = new MainViewModel(outputDir, connectionString, Dispatcher);

        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }

    private static string ReadInstallRoot()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.shared.json");
        if (!File.Exists(settingsPath)) return WxPaths.DefaultInstallRoot;

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("InstallRoot", out var prop))
                return prop.GetString() ?? WxPaths.DefaultInstallRoot;
        }
        catch { /* fall through */ }

        return WxPaths.DefaultInstallRoot;
    }

    private static string ReadConnectionString()
    {
        const string fallback = @"Server=.\SQLEXPRESS;Database=WeatherData;Trusted_Connection=True;TrustServerCertificate=True;";
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.shared.json");
        if (!File.Exists(settingsPath)) return fallback;

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("ConnectionStrings", out var cs) &&
                cs.TryGetProperty("WeatherData", out var prop))
                return prop.GetString() ?? fallback;
        }
        catch { /* fall through */ }

        return fallback;
    }
}
