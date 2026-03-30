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
    })
    .Build();

try
{
    var dbOptions = host.Services.GetRequiredService<DbContextOptions<WeatherDataContext>>();
    await using (var db = new WeatherDataContext(dbOptions))
    {
        await db.Database.EnsureCreatedAsync();
        Logger.Info("Database ready.");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}
