// WxManager — Weather service management GUI.
// Replaces WxAddRecipient with a tabbed WPF application.

using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Windows;

namespace WxManager;

/// <summary>
/// WPF application entry point for WxManager.
/// Loads configuration from <c>appsettings.shared.json</c>, builds
/// <see cref="DbOptions"/>, and opens <see cref="MainWindow"/>.
/// </summary>
public partial class App : Application
{
    /// <summary>EF Core options pointing at the WeatherData SQL Server database.</summary>
    public static DbContextOptions<WeatherDataContext> DbOptions { get; private set; } = null!;

    /// <summary>Fetch bounding-box home latitude from <c>Fetch:HomeLatitude</c>, or <see langword="null"/> if not configured.</summary>
    public static double? FetchHomeLat { get; private set; }

    /// <summary>Fetch bounding-box home longitude from <c>Fetch:HomeLongitude</c>, or <see langword="null"/> if not configured.</summary>
    public static double? FetchHomeLon { get; private set; }

    /// <summary>Fetch bounding-box half-width in degrees from <c>Fetch:BoundingBoxDegrees</c>, or <see langword="null"/> if not configured.</summary>
    public static double? FetchBoxDeg { get; private set; }

    /// <summary>Default language for new recipients from <c>Report:DefaultLanguage</c>. Defaults to <c>"English"</c>.</summary>
    public static string DefaultLanguage { get; private set; } = "English";

    /// <summary>
    /// Builds the database connection, loads optional fetch/report config,
    /// then shows <see cref="MainWindow"/>. Shuts down if the connection string is missing.
    /// </summary>
    /// <param name="e">Startup event arguments (unused).</param>
    /// <sideeffects>
    /// Reads <c>appsettings.shared.json</c> and <c>appsettings.local.json</c>.
    /// Sets static <see cref="DbOptions"/>, <see cref="FetchHomeLat"/>,
    /// <see cref="FetchHomeLon"/>, <see cref="FetchBoxDeg"/>, and <see cref="DefaultLanguage"/>.
    /// Opens and shows <see cref="MainWindow"/>, or shows an error dialog and calls <see cref="Shutdown"/> if configuration is invalid.
    /// </sideeffects>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.shared.json", optional: false)
            .AddJsonFile("appsettings.local.json",  optional: true)
            .Build();

        var connStr = config.GetConnectionString("WeatherData");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            MessageBox.Show(
                "ConnectionStrings:WeatherData not found in configuration.",
                "WxManager — Configuration Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        DbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer(connStr)
            .Options;

        FetchHomeLat = TryParseDouble(config["Fetch:HomeLatitude"]);
        FetchHomeLon = TryParseDouble(config["Fetch:HomeLongitude"]);
        FetchBoxDeg  = TryParseDouble(config["Fetch:BoundingBoxDegrees"]);
        DefaultLanguage = config["Report:DefaultLanguage"] ?? "English";

        new MainWindow().Show();
    }

    /// <summary>
    /// Parses a nullable configuration string as a double using invariant culture.
    /// </summary>
    /// <param name="value">The raw configuration string value, or <see langword="null"/>.</param>
    /// <returns>The parsed double, or <see langword="null"/> if the value is absent or unparseable.</returns>
    private static double? TryParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
