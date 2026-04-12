// WxMonitor Windows Service
// Watches log files and heartbeats for WxParser.Svc and WxReport.Svc;
// sends alert emails when errors are detected or a service goes silent.
//
// Install:   sc.exe create WxMonitorSvc binPath= "<path>\WxMonitor.Svc.exe"
// Uninstall: sc.exe delete WxMonitorSvc
// Start:     sc.exe start WxMonitorSvc
// Stop:      sc.exe stop WxMonitorSvc

using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using WxMonitor.Svc;
using WxServices.Common;
using WxServices.Logging;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.LogFile("wxmonitor-svc"));
Logger.Info($"WxMonitor.Svc {WxPaths.ProductVersion} (commit {WxPaths.GitCommit}) starting.");

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxMonitorSvc";
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
        services.AddHostedService<MonitorWorker>();
    })
    .Build();

try
{
    var dbOptions = host.Services.GetRequiredService<DbContextOptions<WeatherDataContext>>();
    await DatabaseSetup.EnsureSchemaAsync(dbOptions);
    Logger.Info("Database ready.");

    var cfg = host.Services.GetRequiredService<IConfiguration>();
    await ValidateConfigAsync(cfg, dbOptions);

    await PrerequisiteChecker.LogPrerequisitesAsync(
        PrerequisiteChecker.Requires.SqlServer,
        connectionString: cfg.GetConnectionString("WeatherData") ?? "");

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}

static async Task ValidateConfigAsync(IConfiguration config, DbContextOptions<WeatherDataContext> dbOptions)
{
    var issues = new List<string>();

    await using var ctx = new WeatherDataContext(dbOptions);
    var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);

    if (string.IsNullOrWhiteSpace(gs?.SmtpUsername))    issues.Add("SmtpUsername");
    if (string.IsNullOrWhiteSpace(gs?.SmtpPassword))    issues.Add("SmtpPassword");
    if (string.IsNullOrWhiteSpace(gs?.SmtpFromAddress)) issues.Add("SmtpFromAddress");
    if (string.IsNullOrWhiteSpace(config["Monitor:AlertEmail"])) issues.Add("Monitor:AlertEmail");

    if (issues.Count > 0)
        Logger.Warn($"Missing required configuration — alerts will not send until resolved: {string.Join(", ", issues)}. " +
                    "Use WxManager → Configure to set credentials.");
    else
        Logger.Info("Configuration validated.");
}
