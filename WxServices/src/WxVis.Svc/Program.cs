// WxVis.Svc Windows Service
// Automatically renders synoptic analysis maps, GFS forecast maps, and meteograms.
//
// AnalysisMapWorker:  generates a synoptic analysis map at HH:10 UTC each hour,
//   after WxParser.Svc has had time to ingest the routine hourly METARs.
//
// ForecastMapWorker:  polls the database every 30 s and renders each GFS
//   forecast hour as soon as its grid data has been ingested.
//
// MeteogramWorker:    renders a 24-hour and full-period meteogram for each
//   recipient location after each complete GFS run, then writes a manifest JSON.
//
// Install:   sc.exe create WxVisSvc binPath= "<path>\WxVis.Svc.exe"
// Uninstall: sc.exe delete WxVisSvc
// Start:     sc.exe start WxVisSvc
// Stop:      sc.exe stop WxVisSvc

using MetarParser.Data;
using MetarParser.Data.Configuration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

using OpenTelemetry.Metrics;

using WxServices.Common;
using WxServices.Logging;

using WxVis.Svc;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.ServiceLogFile(WxServiceToken.WxVis));
Logger.Info(WxPaths.StartupBanner());

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxVisSvc";
    })
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
           .AddJsonFile(new PhysicalFileProvider(installRoot), "appsettings.local.json", optional: true, reloadOnChange: true)
           // Single source of truth for InstallRoot (WX-65, same fix WX-64 applied to WxMonitor/WxReport):
           // the map workers resolve ScriptDir + OutputDir via IConfiguration["InstallRoot"]. Without this,
           // that key stays the shared-config C:\HarderWare, so in the container the workers look for the
           // Python scripts under a non-existent Windows path and hand it to Python as the plots output
           // dir — renders never reach the host mount. Must come last so it wins. No-op on Windows.
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

        services.AddWxTelemetry(ctx.Configuration, m => m
            .AddMeter("WxVis.Svc")
            .AddView("wxvis.render.duration.seconds",
                new ExplicitBucketHistogramConfiguration { Boundaries = [5, 10, 20, 30, 60, 120, 300] }));

        services.AddSingleton(dbOptions);
        services.AddHostedService<AnalysisMapWorker>();
        services.AddHostedService<ForecastMapWorker>();
        services.AddHostedService<MeteogramWorker>();
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

    await PrerequisiteChecker.LogPrerequisitesAsync(
        PrerequisiteChecker.Requires.SqlServer | PrerequisiteChecker.Requires.CondaPython | PrerequisiteChecker.Requires.WxVisPackages,
        connectionString: cfg.GetConnectionString("WeatherData") ?? "",
        condaPythonExe: cfg["WxVis:CondaPythonExe"] ?? "");

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}