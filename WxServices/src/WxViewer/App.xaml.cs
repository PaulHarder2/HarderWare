using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
            Logger.Info($"WxViewer {WxPaths.ProductVersion} (commit {WxPaths.GitCommit}) starting.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Logger.Initialise failed: {ex.Message}");
        }

        // Global exception handlers — log before the process terminates.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var connectionString = ReadConnectionString();

            _ = PrerequisiteChecker.LogPrerequisitesAsync(
                PrerequisiteChecker.Requires.SqlServer,
                connectionString: connectionString);
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

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Fatal("Unhandled dispatcher exception.", e.Exception);
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Logger.Fatal("Unhandled AppDomain exception.", ex);
        else
            Logger.Fatal($"Unhandled AppDomain exception: {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
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
        catch (Exception ex) { Logger.Warn($"Failed to read InstallRoot from appsettings: {ex.Message}"); }

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
        catch (Exception ex) { Logger.Warn($"Failed to read ConnectionString from appsettings: {ex.Message}"); }

        return fallback;
    }
}
