// WxManager — Weather service management GUI.
// Replaces WxAddRecipient with a tabbed WPF application.

using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Globalization;
using System.Windows;
using WxServices.Common;
using WxServices.Logging;

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

    /// <summary>Loaded application configuration (shared + local overrides).</summary>
    public static IConfiguration? Configuration { get; private set; }

    /// <summary>Fetch bounding-box home latitude from <c>Fetch:HomeLatitude</c>, or <see langword="null"/> if not configured.</summary>
    public static double? FetchHomeLat { get; private set; }

    /// <summary>Fetch bounding-box home longitude from <c>Fetch:HomeLongitude</c>, or <see langword="null"/> if not configured.</summary>
    public static double? FetchHomeLon { get; private set; }

    /// <summary>Fetch bounding-box half-width in degrees from <c>Fetch:BoundingBoxDegrees</c>, or <see langword="null"/> if not configured.</summary>
    public static double? FetchBoxDeg { get; private set; }

    /// <summary>Resolved fetch region (explicit bounds or derived from home lat/lon + bounding box).</summary>
    public static FetchRegion? FetchRegion { get; private set; }

    /// <summary>Default language for new recipients from <c>Report:DefaultLanguage</c>. Defaults to <c>"English"</c>.</summary>
    public static string DefaultLanguage { get; private set; } = "English";

    /// <summary>Default scheduled send hour for new recipients from <c>Report:DefaultScheduledSendHour</c>. Defaults to <c>7</c>.</summary>
    public static int DefaultScheduledSendHour { get; private set; } = 7;

    /// <summary>SMTP connection and credential settings for sending announcements.</summary>
    public static SmtpConfig SmtpConfig { get; private set; } = new SmtpConfig();

    /// <summary>Anthropic API key for Claude (loaded from <c>GlobalSettings</c> DB at runtime).</summary>
    public static string ClaudeApiKey { get; private set; } = "";

    /// <summary>Claude model ID from <c>Claude:Model</c>. Defaults to <c>"claude-sonnet-4-6"</c>.</summary>
    public static string ClaudeModel { get; private set; } = "claude-sonnet-4-6";

    /// <summary>Anthropic Messages API endpoint from <c>Claude:MessagesEndpoint</c>.</summary>
    public static string ClaudeMessagesEndpoint { get; private set; } = "https://api.anthropic.com/v1/messages";

    /// <summary>Anthropic API version header value from <c>Claude:ApiVersion</c>.</summary>
    public static string ClaudeApiVersion { get; private set; } = "2023-06-01";

    /// <summary>Maximum tokens for Claude responses from <c>Claude:MaxTokens</c>. Defaults to <c>2048</c>.</summary>
    public static int ClaudeMaxTokens { get; private set; } = 2048;

    /// <summary>Maximum candidate stations evaluated during nearby-station lookup, from <c>WxManager:MaxNearbyStationsInLookup</c>. Defaults to <c>40</c>.</summary>
    public static int MaxNearbyStationsInLookup { get; private set; } = 40;

    /// <summary>Search radius in kilometres for nearby-station lookup, from <c>WxManager:StationLookupRadiusKm</c>. Defaults to <c>150.0</c>.</summary>
    public static double StationLookupRadiusKm { get; private set; } = 150.0;

    /// <summary>Maximum number of nearby stations to display after filtering, from <c>WxManager:MaxDisplayStations</c>. Defaults to <c>5</c>.</summary>
    public static int MaxDisplayStations { get; private set; } = 5;

    /// <summary>Default IANA timezone ID for new recipients, from <c>WxManager:DefaultTimezone</c>. Defaults to <c>"America/Chicago"</c>.</summary>
    public static string DefaultTimezone { get; private set; } = "America/Chicago";

    /// <summary>HTTP User-Agent header sent to external APIs, from <c>WxManager:UserAgent</c>. Defaults to <c>"WxManager/1.0"</c>.</summary>
    public static string UserAgent { get; private set; } = "WxManager/1.0";

    /// <summary>Base URL for the Aviation Weather Center METAR API, from <c>WxManager:AwcMetarEndpoint</c>.</summary>
    public static string AwcMetarEndpoint { get; private set; } = "https://aviationweather.gov/api/data/metar";

    /// <summary>Lookback window in hours for AWC METAR queries, from <c>WxManager:AwcMetarHours</c>. Defaults to <c>6</c>.</summary>
    public static int AwcMetarHours { get; private set; } = 6;

    /// <summary>Auto-dismiss delay in milliseconds for success messages, from <c>WxManager:SuccessMessageDismissMs</c>. Defaults to <c>3000</c>.</summary>
    public static int SuccessMessageDismissMs { get; private set; } = 3000;

    /// <summary>
    /// Builds the database connection, loads optional fetch/report/announce config,
    /// then shows <see cref="MainWindow"/>. Shuts down if the connection string is missing.
    /// </summary>
    /// <param name="e">Startup event arguments (unused).</param>
    /// <sideeffects>
    /// Reads <c>appsettings.shared.json</c> and <c>{InstallRoot}\appsettings.local.json</c>.
    /// Sets all static properties on <see cref="App"/>.
    /// Opens and shows <see cref="MainWindow"/>, or shows an error dialog and calls <see cref="Shutdown"/> if configuration is invalid.
    /// </sideeffects>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var wxPaths = new WxPaths(WxPaths.ReadInstallRoot());
        try
        {
            Logger.Initialise(wxPaths.LogFile("wxmanager"));
            Logger.Info($"WxManager {WxPaths.ProductVersion} (commit {WxPaths.GitCommit}) starting.");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Logger.Initialise failed:\n{ex}",
                "WxManager — Logging Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.shared.json", optional: false)
            .AddJsonFile(new PhysicalFileProvider(wxPaths.InstallRoot), "appsettings.local.json", optional: true, reloadOnChange: false)
            .Build();

        Configuration = config;

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

        FetchHomeLat    = TryParseDouble(config["Fetch:HomeLatitude"]);
        FetchHomeLon    = TryParseDouble(config["Fetch:HomeLongitude"]);
        FetchBoxDeg     = TryParseDouble(config["Fetch:BoundingBoxDegrees"]);
        FetchRegion     = FetchRegion.FromConfig(key => config[key]);
        DefaultLanguage = config["Report:DefaultLanguage"] ?? "English";
        if (int.TryParse(config["Report:DefaultScheduledSendHour"], out var defHour) && defHour >= 0 && defHour <= 23)
            DefaultScheduledSendHour = defHour;
        SmtpConfig             = config.GetSection("Smtp").Get<SmtpConfig>() ?? new SmtpConfig();
        ClaudeApiKey           = config["Claude:ApiKey"]           ?? "";
        ClaudeModel            = config["Claude:Model"]            ?? "claude-sonnet-4-6";
        ClaudeMessagesEndpoint = config["Claude:MessagesEndpoint"] ?? ClaudeMessagesEndpoint;
        ClaudeApiVersion       = config["Claude:ApiVersion"]       ?? ClaudeApiVersion;
        if (int.TryParse(config["Claude:MaxTokens"], out var maxTok) && maxTok > 0)
            ClaudeMaxTokens = maxTok;
        if (int.TryParse(config["WxManager:MaxNearbyStationsInLookup"], out var maxNearby) && maxNearby > 0)
            MaxNearbyStationsInLookup = maxNearby;
        if (TryParseDouble(config["WxManager:StationLookupRadiusKm"]) is { } radiusKm && radiusKm > 0)
            StationLookupRadiusKm = radiusKm;
        if (int.TryParse(config["WxManager:MaxDisplayStations"], out var maxDisplay) && maxDisplay > 0)
            MaxDisplayStations = maxDisplay;
        DefaultTimezone        = config["WxManager:DefaultTimezone"]        ?? DefaultTimezone;
        UserAgent              = config["WxManager:UserAgent"]              ?? UserAgent;
        AwcMetarEndpoint       = config["WxManager:AwcMetarEndpoint"]       ?? AwcMetarEndpoint;
        if (int.TryParse(config["WxManager:AwcMetarHours"], out var awcHours) && awcHours > 0)
            AwcMetarHours = awcHours;
        if (int.TryParse(config["WxManager:SuccessMessageDismissMs"], out var dismissMs) && dismissMs >= 0)
            SuccessMessageDismissMs = dismissMs;

        new MainWindow().Show();
    }

    /// <summary>Logs application exit.</summary>
    /// <param name="e">Exit event arguments (unused).</param>
    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Info("WxManager exiting.");
        base.OnExit(e);
    }

    /// <summary>
    /// Parses a nullable configuration string as a double using invariant culture.
    /// </summary>
    /// <param name="value">The raw configuration string value, or <see langword="null"/>.</param>
    /// <returns>The parsed double, or <see langword="null"/> if the value is absent or unparseable.</returns>
    private static double? TryParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
