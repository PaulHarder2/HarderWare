// WxParser Windows Service
// Fetches METAR and TAF reports on a configurable interval and stores them in the database.
//
// Install:   sc.exe create WxParserSvc binPath= "<path>\WxParser.Svc.exe"
// Uninstall: sc.exe delete WxParserSvc
// Start:     sc.exe start WxParserSvc
// Stop:      sc.exe stop WxParserSvc

using MetarParser.Data;
using WxServices.Logging;
using WxParser.Svc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

Logger.Initialise();
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

        services.AddSingleton(dbOptions);
        services.AddHostedService<FetchWorker>();
        services.AddHostedService<GfsFetchWorker>();
    })
    .Build();

try
{
    var dbOptions = host.Services.GetRequiredService<DbContextOptions<WeatherDataContext>>();
    await using (var db = new WeatherDataContext(dbOptions))
    {
        await db.Database.EnsureCreatedAsync();

        // EnsureCreatedAsync only creates the DB when it is entirely absent;
        // it will not add tables that are missing from an existing database.
        // Create GfsGrid explicitly so schema additions survive without a full DB rebuild.
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GfsModelRuns')
            BEGIN
                CREATE TABLE [GfsModelRuns] (
                    [ModelRunUtc] datetime2 NOT NULL,
                    [IsComplete]  bit       NOT NULL DEFAULT 0,
                    CONSTRAINT [PK_GfsModelRuns] PRIMARY KEY ([ModelRunUtc])
                );
            END");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GfsGrid')
            BEGIN
                CREATE TABLE [GfsGrid] (
                    [Id]           int      NOT NULL IDENTITY,
                    [ModelRunUtc]  datetime2 NOT NULL,
                    [ForecastHour] int       NOT NULL,
                    [Lat]          real      NOT NULL,
                    [Lon]          real      NOT NULL,
                    [TmpC]         real      NULL,
                    [DwpC]         real      NULL,
                    [UGrdMs]       real      NULL,
                    [VGrdMs]       real      NULL,
                    [PRateKgM2s]   real      NULL,
                    [TcdcPct]      real      NULL,
                    [CapeJKg]      real      NULL,
                    CONSTRAINT [PK_GfsGrid] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [UX_GfsGrid_Run_Hour_LatLon]
                    ON [GfsGrid] ([ModelRunUtc], [ForecastHour], [Lat], [Lon]);
                CREATE INDEX [IX_GfsGrid_Run_Hour]
                    ON [GfsGrid] ([ModelRunUtc], [ForecastHour]);
            END");

        Logger.Info("Database ready.");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}
