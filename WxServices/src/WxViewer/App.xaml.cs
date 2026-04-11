using System.IO;
using System.Text.Json;
using System.Windows;
using WxServices.Common;
using WxServices.Logging;

namespace WxViewer;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new WxPaths(ReadInstallRoot());

        try
        {
            Logger.Initialise(paths.LogFile("wxviewer"));
            Logger.Info("WxViewer starting.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Logger.Initialise failed: {ex.Message}");
        }

        try
        {
            var connectionString = ReadConnectionString();
            _viewModel = new MainViewModel(paths.PlotsDir, connectionString, Dispatcher);

            var window = new MainWindow(_viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during startup.", ex);
            MessageBox.Show($"WxViewer failed to start:\n{ex.Message}",
                "WxViewer — Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("WxViewer exiting.");
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
