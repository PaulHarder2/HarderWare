// WxReport Windows Service
// Periodically generates weather reports via Claude and emails them to configured recipients.
//
// Install:   sc.exe create WxReportSvc binPath= "<path>\WxReport.Svc.exe"
// Uninstall: sc.exe delete WxReportSvc
// Start:     sc.exe start WxReportSvc
// Stop:      sc.exe stop WxReportSvc

using MetarParser.Data;
using MetarParser.Data.Entities;
using WxServices.Common;
using WxServices.Logging;
using WxReport.Svc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.LogFile("wxreport-svc"));
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
           .AddJsonFile(new PhysicalFileProvider(installRoot), "appsettings.local.json", optional: true, reloadOnChange: true)
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
    await DatabaseSetup.EnsureSchemaAsync(dbOptions);
    Logger.Info("Database ready.");

    // ── One-time migration: seed Recipients and GlobalSettings from config ────────
    // If the Recipients table is empty and the config has recipients, move them to
    // the database so the service can operate without appsettings.local.json.
    // If GlobalSettings fields are null and the config has the corresponding values,
    // copy them across.  Both operations are idempotent.
    var appConfig = host.Services.GetRequiredService<IConfiguration>();
    await using var seedCtx = new WeatherDataContext(dbOptions);

    if (!await seedCtx.Recipients.AnyAsync())
    {
        var configRecipients = new List<RecipientConfig>();
        appConfig.GetSection("Report:Recipients").Bind(configRecipients);

        var valid = configRecipients.Where(r => !string.IsNullOrWhiteSpace(r.Id)).ToList();
        if (valid.Count > 0)
        {
            foreach (var r in valid)
            {
                seedCtx.Recipients.Add(new Recipient
                {
                    RecipientId        = r.Id!,
                    Email              = r.Email,
                    Name               = r.Name,
                    Language           = r.Language,
                    Timezone           = r.Timezone,
                    ScheduledSendHours = r.ScheduledSendHours,
                    Address            = r.Address,
                    LocalityName       = r.LocalityName,
                    Latitude           = r.Latitude,
                    Longitude          = r.Longitude,
                    MetarIcao          = r.MetarIcao,
                    TafIcao            = r.TafIcao,
                    TempUnit           = r.Units.Temperature,
                    PressureUnit       = r.Units.Pressure,
                    WindSpeedUnit      = r.Units.WindSpeed,
                });
            }
            await seedCtx.SaveChangesAsync();
            Logger.Info($"Seeded {valid.Count} recipient(s) from config into Recipients table.");
        }
    }

    var gs = await seedCtx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);
    if (gs != null)
    {
        var changed = false;

        if (gs.ClaudeApiKey is null)
        {
            var v = appConfig["Claude:ApiKey"];
            if (!string.IsNullOrWhiteSpace(v)) { gs.ClaudeApiKey = v; changed = true; }
        }
        if (gs.SmtpUsername is null)
        {
            var v = appConfig["Smtp:Username"];
            if (!string.IsNullOrWhiteSpace(v)) { gs.SmtpUsername = v; changed = true; }
        }
        if (gs.SmtpPassword is null)
        {
            var v = appConfig["Smtp:Password"];
            if (!string.IsNullOrWhiteSpace(v)) { gs.SmtpPassword = v; changed = true; }
        }
        if (gs.SmtpFromAddress is null)
        {
            var v = appConfig["Smtp:FromAddress"];
            if (!string.IsNullOrWhiteSpace(v)) { gs.SmtpFromAddress = v; changed = true; }
        }

        if (changed)
        {
            await seedCtx.SaveChangesAsync();
            Logger.Info("Seeded GlobalSettings secrets from config.");
        }
    }

    await ValidateConfigAsync(appConfig, dbOptions);

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

    if (string.IsNullOrWhiteSpace(gs?.SmtpUsername    ?? config["Smtp:Username"]))    issues.Add("SmtpUsername");
    if (string.IsNullOrWhiteSpace(gs?.SmtpPassword    ?? config["Smtp:Password"]))    issues.Add("SmtpPassword");
    if (string.IsNullOrWhiteSpace(gs?.SmtpFromAddress ?? config["Smtp:FromAddress"])) issues.Add("SmtpFromAddress");
    if (string.IsNullOrWhiteSpace(gs?.ClaudeApiKey    ?? config["Claude:ApiKey"]))    issues.Add("ClaudeApiKey");

    var recipientCount = await ctx.Recipients.CountAsync();
    if (recipientCount == 0) issues.Add("Recipients (none in database or config)");

    if (issues.Count > 0)
        Logger.Warn($"Missing required configuration — reports will not send until resolved: {string.Join(", ", issues)}. " +
                    "Set secrets via GlobalSettings (database) or appsettings.local.json.");
    else
        Logger.Info("Configuration validated.");
}
