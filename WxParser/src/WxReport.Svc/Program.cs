// WxReport Windows Service
// Periodically generates weather reports via Claude and emails them to configured recipients.
//
// Install:   sc.exe create WxReportSvc binPath= "<path>\WxReport.Svc.exe"
// Uninstall: sc.exe delete WxReportSvc
// Start:     sc.exe start WxReportSvc
// Stop:      sc.exe stop WxReportSvc

using MetarParser.Data;
using WxParser.Logging;
using WxReport.Svc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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

        Logger.Info("Database ready.");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}
