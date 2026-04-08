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
using Microsoft.Extensions.FileProviders;
using OpenTelemetry.Metrics;

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
           .AddJsonFile(new PhysicalFileProvider(@"C:\HarderWare"), "appsettings.local.json", optional: true, reloadOnChange: true)
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

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Recipients')
            BEGIN
                CREATE TABLE [Recipients] (
                    [Id]                int            NOT NULL IDENTITY,
                    [RecipientId]       nvarchar(100)  NOT NULL,
                    [Email]             nvarchar(200)  NOT NULL,
                    [Name]              nvarchar(200)  NOT NULL,
                    [Language]          nvarchar(50)   NULL,
                    [Timezone]          nvarchar(100)  NOT NULL DEFAULT 'UTC',
                    [ScheduledSendHours] nvarchar(50)  NULL,
                    [Address]           nvarchar(500)  NULL,
                    [LocalityName]      nvarchar(200)  NULL,
                    [Latitude]          float          NULL,
                    [Longitude]         float          NULL,
                    [MetarIcao]         nvarchar(100)  NULL,
                    [TafIcao]           nvarchar(10)   NULL,
                    [TempUnit]          nvarchar(10)   NOT NULL DEFAULT 'F',
                    [PressureUnit]      nvarchar(10)   NOT NULL DEFAULT 'inHg',
                    [WindSpeedUnit]     nvarchar(10)   NOT NULL DEFAULT 'mph',
                    CONSTRAINT [PK_Recipients] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [UX_Recipients_RecipientId]
                    ON [Recipients] ([RecipientId]);
            END");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GlobalSettings')
            BEGIN
                CREATE TABLE [GlobalSettings] (
                    [Id]              int            NOT NULL,
                    [ClaudeApiKey]    nvarchar(500)  NULL,
                    [SmtpUsername]    nvarchar(200)  NULL,
                    [SmtpPassword]    nvarchar(200)  NULL,
                    [SmtpFromAddress] nvarchar(200)  NULL,
                    CONSTRAINT [PK_GlobalSettings] PRIMARY KEY ([Id])
                );
                INSERT INTO [GlobalSettings] ([Id]) VALUES (1);
            END");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'Municipality'
            )
            ALTER TABLE [WxStations] ADD [Municipality] nvarchar(100) NULL;");

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'AlwaysFetchDirect'
            )
            ALTER TABLE [WxStations] ADD [AlwaysFetchDirect] bit NULL;");

        Logger.Info("Database ready.");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}

