// WxMonitor Windows Service
// Watches log files and heartbeats for WxParser.Svc and WxReport.Svc;
// sends alert emails when errors are detected or a service goes silent.
//
// Install:   sc.exe create WxMonitorSvc binPath= "<path>\WxMonitor.Svc.exe"
// Uninstall: sc.exe delete WxMonitorSvc
// Start:     sc.exe start WxMonitorSvc
// Stop:      sc.exe stop WxMonitorSvc

using MetarParser.Data;
using MetarParser.Data.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

using OpenTelemetry.Metrics;

using WxMonitor.Svc;

using WxServices.Common;
using WxServices.Logging;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.ServiceLogFile(WxServiceToken.WxMonitor));
Logger.Info(WxPaths.StartupBanner());

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxMonitorSvc";
    })
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
           .AddJsonFile(new PhysicalFileProvider(installRoot), "appsettings.local.json", optional: true, reloadOnChange: true)
           // Single source of truth (WX-64, fixing a WX-63 regression): MonitorWorker resolves the
           // watched services' heartbeat + log paths via IConfiguration["InstallRoot"]. Without this,
           // that key stays the shared-config C:\HarderWare, so in the container every file watcher
           // looked under a non-existent Windows path — the heartbeat watcher logged "heartbeat file
           // not found" every cycle regardless of service health (only the DB-based METAR-staleness
           // watcher still worked). Must come last so it wins. No-op on Windows (same value).
           .AddInstallRoot(installRoot);

        // DB-backed config overlay (WX-313): the Config table is the runtime source of
        // truth for application config, layered LAST so it wins over the JSON files.
        // AddDatabaseConfig resolves the connection string from the file layers just added.
        // On a fresh DB the Config table may not exist until EnsureSchemaAsync runs, so this
        // first load overlays nothing; IConfigurationRoot.Reload() re-runs it after
        // schema-ensure (below).
        cfg.AddDatabaseConfig();
    })
    .ConfigureServices((ctx, services) =>
    {
        var connectionString = ctx.Configuration.GetConnectionString("WeatherData")
            ?? throw new InvalidOperationException(
                "Connection string 'WeatherData' not found in configuration.");

        var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer(connectionString)
            .Options;

        services.AddWxTelemetry(ctx.Configuration, m => m.AddMeter("WxMonitor.Svc"));

        services.AddSingleton(dbOptions);
        services.AddSingleton<Func<SmtpConfig, IEmailer>>(_ => smtp => new SmtpSender(smtp, "WxMonitor"));
        services.AddSingleton<IMonitorStateStore>(_ => new MonitorStateStore());
        services.AddSingleton<Func<DateTime>>(_ => () => DateTime.UtcNow);
        services.AddHostedService<MonitorWorker>();
    })
    .Build();

try
{
    var dbOptions = host.Services.GetRequiredService<DbContextOptions<WeatherDataContext>>();
    var cfg = host.Services.GetRequiredService<IConfiguration>();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    await DatabaseSetup.EnsureSchemaAsync(
        dbOptions,
        DatabaseStartupRetryOptions.FromConfiguration(cfg),
        lifetime.ApplicationStopping);
    Logger.Info("Database ready.");

    // The Config table is guaranteed to exist now (EnsureSchemaAsync ran), so
    // re-run the DB config provider to overlay its values before configuration is
    // read below: the provider's first load during host build ran before the
    // schema existed and overlaid nothing (WX-313).
    ((IConfigurationRoot)cfg).Reload();

    await ValidateConfigAsync(cfg, dbOptions);

    await PrerequisiteChecker.LogPrerequisitesAsync(
        PrerequisiteChecker.Requires.SqlServer,
        connectionString: cfg.GetConnectionString("WeatherData") ?? "");

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}

static async Task ValidateConfigAsync(IConfiguration config, DbContextOptions<WeatherDataContext> dbOptions)
{
    var issues = new List<string>();

    await using var ctx = new WeatherDataContext(dbOptions);
    var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);

    if (string.IsNullOrWhiteSpace(gs?.SmtpUsername)) issues.Add("SmtpUsername");
    if (string.IsNullOrWhiteSpace(gs?.SmtpPassword)) issues.Add("SmtpPassword");
    if (string.IsNullOrWhiteSpace(gs?.SmtpFromAddress)) issues.Add("SmtpFromAddress");
    if (string.IsNullOrWhiteSpace(config["Monitor:AlertEmail"])) issues.Add("Monitor:AlertEmail");

    if (issues.Count > 0)
        Logger.Warn($"Missing required configuration — alerts will not send until resolved: {string.Join(", ", issues)}. " +
                    "Use WxManager → Configure to set credentials.");
    else
        Logger.Info("Configuration validated.");
}