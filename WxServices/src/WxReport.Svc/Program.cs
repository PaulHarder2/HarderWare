// WxReport Windows Service
// Periodically generates weather reports via Claude and emails them to configured recipients.
//
// Install:   sc.exe create WxReportSvc binPath= "<path>\WxReport.Svc.exe"
// Uninstall: sc.exe delete WxReportSvc
// Start:     sc.exe start WxReportSvc
// Stop:      sc.exe stop WxReportSvc

using MetarParser.Data;
using WxServices.Common;
using WxServices.Logging;
using WxReport.Svc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using OpenTelemetry.Metrics;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.LogFile("wxreport-svc"));
Logger.Info($"WxReport.Svc {WxPaths.ProductVersion} (commit {WxPaths.GitCommit}) starting.");

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxReportSvc";
    })
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
           .AddJsonFile(new PhysicalFileProvider(installRoot), "appsettings.local.json", optional: true, reloadOnChange: true);
    })
    .ConfigureServices((ctx, services) =>
    {
        var connectionString = ctx.Configuration.GetConnectionString("WeatherData")
            ?? throw new InvalidOperationException(
                "Connection string 'WeatherData' not found in configuration.");

        var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer(connectionString)
            .Options;

        var telemetryEnabled = ctx.Configuration.GetValue<bool>("Telemetry:Enabled", false);
        var otlpEndpoint = ctx.Configuration["Telemetry:OtlpEndpoint"] ?? "http://localhost:4318/v1/metrics";

        services.AddOpenTelemetry()
            .WithMetrics(m =>
            {
                m.AddMeter("WxReport.Svc")
                 .AddView("wxreport.cycle.duration.seconds",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [1, 2, 5, 10, 20, 30, 60, 120]
                    })
                 .AddView("wxreport.claude.duration.seconds",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [1, 2, 5, 10, 15, 20, 30, 60]
                    });

                if (telemetryEnabled)
                {
                    m.AddOtlpExporter((o, r) =>
                    {
                        o.Endpoint = new Uri(otlpEndpoint);
                        o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                        r.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000;
                    });
                    Logger.Info($"Telemetry enabled. Exporting metrics to {otlpEndpoint}.");
                }
                else
                {
                    Logger.Info("Telemetry disabled. Set Telemetry:Enabled=true in appsettings to export metrics.");
                }
            });

        services.AddSingleton(dbOptions);
        services.AddSingleton(LoadPersonaPrefix());
        services.AddHttpClient("WxReport", c =>
        {
            c.DefaultRequestHeaders.Add("User-Agent", "WxReport/1.0");
        });
        services.AddHostedService<ReportWorker>();
    })
    .Build();

try
{
    var dbOptions = host.Services.GetRequiredService<DbContextOptions<WeatherDataContext>>();
    var appConfig = host.Services.GetRequiredService<IConfiguration>();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    await DatabaseSetup.EnsureSchemaAsync(
        dbOptions,
        DatabaseStartupRetryOptions.FromConfiguration(appConfig),
        lifetime.ApplicationStopping);
    Logger.Info("Database ready.");

    await ValidateConfigAsync(dbOptions);

    await PrerequisiteChecker.LogPrerequisitesAsync(
        PrerequisiteChecker.Requires.SqlServer,
        connectionString: appConfig.GetConnectionString("WeatherData") ?? "");

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}

// Loads AboutPaul.md from the service's binary directory. The file ships
// alongside the executable via the <Content> include in WxReport.Svc.csproj
// (source of truth at the repo root). A missing file is treated as a fatal
// startup error: the persona prefix is required for every Claude call, and
// silently falling back to generic-Claude output would be a worse failure
// mode than refusing to start.
static PersonaPrefix LoadPersonaPrefix()
{
    var path = Path.Combine(AppContext.BaseDirectory, "AboutPaul.md");
    if (!File.Exists(path))
    {
        Logger.Error($"AboutPaul.md not found at {path}. The persona prefix is required for WxReport.Svc to start.");
        throw new FileNotFoundException("AboutPaul.md not found", path);
    }
    string text;
    try
    {
        text = File.ReadAllText(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Logger.Error($"Failed to read AboutPaul.md at {path}. The persona prefix is required for WxReport.Svc to start.", ex);
        throw;
    }
    if (string.IsNullOrWhiteSpace(text))
    {
        Logger.Error($"AboutPaul.md at {path} is empty or whitespace-only. The persona prefix must have content for WxReport.Svc to start.");
        throw new InvalidOperationException($"AboutPaul.md at {path} is empty or whitespace-only.");
    }
    Logger.Info($"Loaded persona prefix from {path} ({text.Length} chars).");
    return new PersonaPrefix(text);
}

static async Task ValidateConfigAsync(DbContextOptions<WeatherDataContext> dbOptions)
{
    var issues = new List<string>();

    await using var ctx = new WeatherDataContext(dbOptions);
    var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);

    if (string.IsNullOrWhiteSpace(gs?.SmtpUsername))    issues.Add("SmtpUsername");
    if (string.IsNullOrWhiteSpace(gs?.SmtpPassword))    issues.Add("SmtpPassword");
    if (string.IsNullOrWhiteSpace(gs?.SmtpFromAddress)) issues.Add("SmtpFromAddress");
    if (string.IsNullOrWhiteSpace(gs?.ClaudeApiKey))    issues.Add("ClaudeApiKey");

    var recipientCount = await ctx.Recipients.CountAsync();
    if (recipientCount == 0) issues.Add("Recipients (none in database)");

    if (issues.Count > 0)
        Logger.Warn($"Missing required configuration — reports will not send until resolved: {string.Join(", ", issues)}. " +
                    "Use WxManager → Configure to set credentials.");
    else
        Logger.Info("Configuration validated.");
}
