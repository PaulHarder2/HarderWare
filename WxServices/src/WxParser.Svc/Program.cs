// WxParser Windows Service
// Fetches METAR and TAF reports on a configurable interval and stores them in the database.
//
// Install:   sc.exe create WxParserSvc binPath= "<path>\WxParser.Svc.exe"
// Uninstall: sc.exe delete WxParserSvc
// Start:     sc.exe start WxParserSvc
// Stop:      sc.exe stop WxParserSvc

using MetarParser.Data;
using WxServices.Common;
using WxServices.Logging;
using WxParser.Svc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using OpenTelemetry.Metrics;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.LogFile("wxparser-svc"));
Logger.Info("WxParser.Svc starting.");

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxParserSvc";
    })
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
           .AddJsonFile("appsettings.json",        optional: false, reloadOnChange: true)
           .AddJsonFile(new PhysicalFileProvider(installRoot), "appsettings.local.json", optional: true, reloadOnChange: true)
           .AddJsonFile("appsettings.local.json",  optional: true,  reloadOnChange: true);
    })
    .ConfigureServices((ctx, services) =>
    {
        var connectionString = ctx.Configuration.GetConnectionString("WeatherData")
            ?? throw new InvalidOperationException(
                "Connection string 'WeatherData' not found in appsettings.json.");

        var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer(connectionString)
            .Options;

        var otlpEndpoint = ctx.Configuration["Telemetry:OtlpEndpoint"] ?? "http://localhost:4318/v1/metrics";

        services.AddOpenTelemetry()
            .WithMetrics(m => m
                .AddMeter("WxParser.Svc")
                .AddView("wxparser.fetch.cycle.duration.seconds",
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [1, 2, 5, 10, 20, 30, 60, 120]
                    })
                .AddOtlpExporter((o, r) =>
                {
                    o.Endpoint = new Uri(otlpEndpoint);
                    o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
                    r.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000;
                }));

        services.AddSingleton(dbOptions);
        services.AddHostedService<FetchWorker>();
        services.AddHostedService<GfsFetchWorker>();
    })
    .Build();

try
{
    var dbOptions = host.Services.GetRequiredService<DbContextOptions<WeatherDataContext>>();
    await DatabaseSetup.EnsureSchemaAsync(dbOptions);
    Logger.Info("Database ready.");

    var cfg = host.Services.GetRequiredService<IConfiguration>();
    await PrerequisiteChecker.LogPrerequisitesAsync(
        PrerequisiteChecker.Requires.SqlServer | PrerequisiteChecker.Requires.Wsl | PrerequisiteChecker.Requires.Wgrib2,
        connectionString: cfg.GetConnectionString("WeatherData") ?? "",
        wgrib2WslPath: cfg["Gfs:Wgrib2WslPath"] ?? "/usr/local/bin/wgrib2");

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}

