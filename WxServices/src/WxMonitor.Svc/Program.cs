// WxMonitor Windows Service
// Watches log files and heartbeats for WxParser.Svc and WxReport.Svc;
// sends alert emails when errors are detected or a service goes silent.
//
// Install:   sc.exe create WxMonitorSvc binPath= "<path>\WxMonitor.Svc.exe"
// Uninstall: sc.exe delete WxMonitorSvc
// Start:     sc.exe start WxMonitorSvc
// Stop:      sc.exe stop WxMonitorSvc

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
           .AddJsonFile("appsettings.json",       optional: false, reloadOnChange: true)
           .AddJsonFile("appsettings.local.json",  optional: true,  reloadOnChange: true);
    })
    .ConfigureServices((_, services) =>
    {
        services.AddHostedService<MonitorWorker>();
    })
    .Build();

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}
