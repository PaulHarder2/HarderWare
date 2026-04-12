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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using WxServices.Common;
using WxServices.Logging;
using WxVis.Svc;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.LogFile("wxvis-svc"));
Logger.Info($"WxVis.Svc {WxPaths.ProductVersion} (commit {WxPaths.GitCommit}) starting.");

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxVisSvc";
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

        services.AddSingleton(dbOptions);
        services.AddHostedService<AnalysisMapWorker>();
        services.AddHostedService<ForecastMapWorker>();
        services.AddHostedService<MeteogramWorker>();
    })
    .Build();

try
{
    var cfg = host.Services.GetRequiredService<IConfiguration>();
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
