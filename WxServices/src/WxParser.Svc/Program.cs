// WxParser Windows Service
// Fetches METAR and TAF reports on a configurable interval and stores them in the database.
//
// Install:   sc.exe create WxParserSvc binPath= "<path>\WxParser.Svc.exe"
// Uninstall: sc.exe delete WxParserSvc
// Start:     sc.exe start WxParserSvc
// Stop:      sc.exe stop WxParserSvc

using MetarParser.Data;
using MetarParser.Data.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

using OpenTelemetry.Metrics;

using WxParser.Svc;

using WxServices.Common;
using WxServices.Logging;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.ServiceLogFile(WxServiceToken.WxParser));
Logger.Info(WxPaths.StartupBanner());

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxParserSvc";
    })
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
           .AddJsonFile(new PhysicalFileProvider(installRoot), "appsettings.local.json", optional: true, reloadOnChange: true)
           // Single source of truth for InstallRoot (WX-66, same fix WX-64/65 applied to the other
           // services): FetchWorker/GfsFetchWorker resolve the heartbeat and the GRIB temp dir via
           // IConfiguration["InstallRoot"]. Without this that key stays the shared-config C:\HarderWare,
           // so in the container they land under a non-existent Windows path. Must come last so it wins.
           // No-op on Windows (same value).
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
                "Connection string 'WeatherData' not found in appsettings.json.");

        var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer(connectionString)
            .Options;

        services.AddWxTelemetry(ctx.Configuration, m => m
            .AddMeter("WxParser.Svc")
            .AddMeter("WxParser.Svc.Gfs")
            .AddView("wxparser.fetch.cycle.duration.seconds",
                new ExplicitBucketHistogramConfiguration { Boundaries = [1, 2, 5, 10, 20, 30, 60, 120] })
            .AddView("wxparser.gfs.cycle.duration.seconds",
                new ExplicitBucketHistogramConfiguration { Boundaries = [30, 60, 120, 300, 600, 900, 1800] }));

        services.AddSingleton(dbOptions);
        services.AddHostedService<FetchWorker>();
        services.AddHostedService<GfsFetchWorker>();
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
    // re-run the DB config provider to overlay its values before anything reads
    // configuration below: the provider's first load during host build ran before
    // the schema existed and overlaid nothing (WX-313).
    ((IConfigurationRoot)cfg).Reload();

    var wgrib2Path = cfg["Gfs:Wgrib2Path"];
    if (string.IsNullOrWhiteSpace(wgrib2Path))
        wgrib2Path = paths.Wgrib2DefaultPath;
    await PrerequisiteChecker.LogPrerequisitesAsync(
        PrerequisiteChecker.Requires.SqlServer | PrerequisiteChecker.Requires.Wgrib2,
        connectionString: cfg.GetConnectionString("WeatherData") ?? "",
        wgrib2Path: wgrib2Path);

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}