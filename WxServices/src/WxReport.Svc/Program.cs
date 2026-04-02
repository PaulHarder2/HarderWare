// WxReport Windows Service
// Periodically generates weather reports via Claude and emails them to configured recipients.
//
// Install:   sc.exe create WxReportSvc binPath= "<path>\WxReport.Svc.exe"
// Uninstall: sc.exe delete WxReportSvc
// Start:     sc.exe start WxReportSvc
// Stop:      sc.exe stop WxReportSvc

using MetarParser.Data;
using WxServices.Logging;
using WxReport.Svc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

Logger.Initialise();
Logger.Info("WxReport.Svc starting.");

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxReportSvc";
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
                "Connection string 'WeatherData' not found in configuration.");

        var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer(connectionString)
            .Options;

        services.AddSingleton(dbOptions);
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
    await using (var db = new WeatherDataContext(dbOptions))
    {
        await db.Database.EnsureCreatedAsync();

        // EnsureCreatedAsync only creates the DB when it is entirely absent;
        // it will not add tables that are missing from an existing database.
        // Create RecipientStates explicitly so schema changes survive a table drop
        // without requiring a full database rebuild.
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RecipientStates')
            BEGIN
                CREATE TABLE [RecipientStates] (
                    [Id]                      int           NOT NULL IDENTITY,
                    [RecipientId]             nvarchar(100) NOT NULL,
                    [LastScheduledSentUtc]    datetime2     NULL,
                    [LastUnscheduledSentUtc]  datetime2     NULL,
                    [LastSnapshotFingerprint] nvarchar(200) NULL,
                    CONSTRAINT [PK_RecipientStates] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [UX_RecipientStates_RecipientId]
                    ON [RecipientStates] ([RecipientId]);
            END");

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
                    [Id]           int       NOT NULL IDENTITY,
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

    ValidateConfig(host.Services.GetRequiredService<IConfiguration>());

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}

static void ValidateConfig(IConfiguration config)
{
    var issues = new List<string>();

    if (string.IsNullOrWhiteSpace(config["Smtp:Username"]))    issues.Add("Smtp:Username");
    if (string.IsNullOrWhiteSpace(config["Smtp:Password"]))    issues.Add("Smtp:Password");
    if (string.IsNullOrWhiteSpace(config["Smtp:FromAddress"])) issues.Add("Smtp:FromAddress");
    if (string.IsNullOrWhiteSpace(config["Claude:ApiKey"]))    issues.Add("Claude:ApiKey");

    var recipientCount = config.GetSection("Report:Recipients").GetChildren().Count();
    if (recipientCount == 0) issues.Add("Report:Recipients (none configured)");

    if (issues.Count > 0)
        Logger.Warn($"Missing required configuration — reports will not send until resolved: {string.Join(", ", issues)}. Set these in appsettings.local.json.");
    else
        Logger.Info("Configuration validated.");
}
