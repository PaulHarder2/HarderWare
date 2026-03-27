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
        Logger.Info("Database ready.");
    }

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}
