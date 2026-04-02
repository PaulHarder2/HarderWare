// WxMonitor Windows Service
// Watches log files and heartbeats for WxParser.Svc and WxReport.Svc;
// sends alert emails when errors are detected or a service goes silent.
//
// Install:   sc.exe create WxMonitorSvc binPath= "<path>\WxMonitor.Svc.exe"
// Uninstall: sc.exe delete WxMonitorSvc
// Start:     sc.exe start WxMonitorSvc
// Stop:      sc.exe stop WxMonitorSvc

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using WxMonitor.Svc;
using WxServices.Logging;

Logger.Initialise();
Logger.Info("WxMonitor.Svc starting.");

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxMonitorSvc";
    })
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
           .AddJsonFile("appsettings.json",        optional: false, reloadOnChange: true)
           .AddJsonFile(new PhysicalFileProvider(@"C:\HarderWare"), "appsettings.local.json", optional: true, reloadOnChange: true)
           .AddJsonFile("appsettings.local.json",  optional: true,  reloadOnChange: true);
    })
    .ConfigureServices((_, services) =>
    {
        services.AddHostedService<MonitorWorker>();
    })
    .Build();

ValidateConfig(host.Services.GetRequiredService<IConfiguration>());

try
{
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
    if (string.IsNullOrWhiteSpace(config["Monitor:AlertEmail"])) issues.Add("Monitor:AlertEmail");

    if (issues.Count > 0)
        Logger.Warn($"Missing required configuration — alerts will not send until resolved: {string.Join(", ", issues)}. Set these in appsettings.local.json.");
    else
        Logger.Info("Configuration validated.");
}
