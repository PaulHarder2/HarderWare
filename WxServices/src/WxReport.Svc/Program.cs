// WxReport Windows Service
// Periodically generates weather reports via Claude and emails them to configured recipients.
//
// Install:   sc.exe create WxReportSvc binPath= "<path>\WxReport.Svc.exe"
// Uninstall: sc.exe delete WxReportSvc
// Start:     sc.exe start WxReportSvc
// Stop:      sc.exe stop WxReportSvc

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

using OpenTelemetry.Metrics;

using WxReport.Svc;
using WxReport.Svc.TranslationQa;

using WxServices.Common;
using WxServices.Logging;

var installRoot = WxPaths.ReadInstallRoot();
var paths = new WxPaths(installRoot);

Logger.Initialise(paths.ServiceLogFile(WxServiceToken.WxReport));
Logger.Info(WxPaths.StartupBanner());

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "WxReportSvc";
    })
    .ConfigureAppConfiguration((_, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: true)
           .AddJsonFile(new PhysicalFileProvider(installRoot), "appsettings.local.json", optional: true, reloadOnChange: true)
           // Single source of truth (WX-64): make IConfiguration["InstallRoot"] — what the workers
           // read to build WxPaths — equal the env-aware root resolved above, not the baked-in
           // shared-config C:\HarderWare. Must come last so it wins. No-op on Windows (same value);
           // in the container it points plots/heartbeat resolution at WXSERVICES_INSTALL_ROOT.
           .AddInstallRoot(installRoot);
    })
    .ConfigureServices((ctx, services) =>
    {
        var connectionString = ctx.Configuration.GetConnectionString("WeatherData")
            ?? throw new InvalidOperationException(
                "Connection string 'WeatherData' not found in configuration.");

        var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
            .UseSqlServer(connectionString)
            .Options;

        services.AddWxTelemetry(ctx.Configuration, m => m
            .AddMeter("WxReport.Svc")
            .AddView("wxreport.cycle.duration.seconds",
                new ExplicitBucketHistogramConfiguration { Boundaries = [1, 2, 5, 10, 20, 30, 60, 120] })
            // Reconciliation (WX-79) routinely runs 60-100s; the old 60s top bucket hid that latency
            // in overflow, masking WX-100. Extend past the 60s cap so the 60-100s tail is resolvable;
            // 360 sits above the default 300s timeout so a timed-out call is distinguishable from a near-ceiling one.
            .AddView("wxreport.claude.duration.seconds",
                new ExplicitBucketHistogramConfiguration { Boundaries = [1, 2, 5, 10, 20, 30, 60, 90, 120, 180, 300, 360] }));

        services.AddSingleton(dbOptions);
        services.AddSingleton(LoadPersonaPrefix());

        // WX-171: the localized-template cache the renderer rewire will read phrases from.
        // Registered here and injected later; a short-lived context per (re)load mirrors
        // the rest of the service's DbContextOptions pattern. Warmed eagerly at startup
        // (below) once the schema is ensured.
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<DbContextOptions<WeatherDataContext>>();
            return new LanguageTemplateStore(
                () =>
                {
                    using var ctx = new WeatherDataContext(opts);
                    return ctx.LanguageTemplates
                        .Include(t => t.Language)
                        .AsNoTracking()
                        .ToList();
                },
                () =>   // WX-238: the concept tokens to anchor in the reconciler prompt glossary.
                {
                    using var ctx = new WeatherDataContext(opts);
                    return ctx.PromptGlossaryTokens.AsNoTracking().Select(g => g.Token).ToList();
                });
        });

        // Geocoding + airport-lookup client. Keeps the 100s HttpClient default so a
        // stalled upstream lookup fails fast; the Claude timeout below must not bleed
        // into this path (WX-100 code-review finding).
        services.AddHttpClient("WxReport", c =>
        {
            c.DefaultRequestHeaders.Add("User-Agent", "WxReport/1.0");
        });

        // Dedicated Claude client. The WX-79 reconciliation pass routinely generates
        // for 60-100s, so the 100s HttpClient default dropped ~1-in-3 reports
        // (WX-100). A separate client raises the ceiling without slowing the
        // fast-fail lookups above. TimeoutSeconds is the single source of truth
        // (same typed ClaudeConfig the worker binds).
        var claudeConfig = ctx.Configuration.GetSection("Claude").Get<ClaudeConfig>() ?? new ClaudeConfig();
        var claudeTimeoutSeconds = claudeConfig.TimeoutSeconds;
        if (claudeTimeoutSeconds <= 0)
        {
            // HttpClient.Timeout rejects zero/negative with ArgumentOutOfRangeException;
            // a misconfigured value must not crash service startup.
            Logger.Warn($"Claude:TimeoutSeconds={claudeTimeoutSeconds} is not positive; " +
                $"falling back to {ClaudeConfig.DefaultTimeoutSeconds}s.");
            claudeTimeoutSeconds = ClaudeConfig.DefaultTimeoutSeconds;
        }
        services.AddHttpClient("Claude", c =>
        {
            c.DefaultRequestHeaders.Add("User-Agent", "WxReport/1.0");
            c.Timeout = TimeSpan.FromSeconds(claudeTimeoutSeconds);
        });
        services.AddHostedService<ReportWorker>();
        services.AddHostedService<QaRerunWorker>();   // WX-235: service-side "Rerun QA" regeneration
    })
    .Build();

try
{
    var dbOptions = host.Services.GetRequiredService<DbContextOptions<WeatherDataContext>>();
    var appConfig = host.Services.GetRequiredService<IConfiguration>();
    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    await DatabaseSetup.EnsureSchemaAsync(
        dbOptions,
        DatabaseStartupRetryOptions.FromConfiguration(appConfig),
        lifetime.ApplicationStopping);
    Logger.Info("Database ready.");

    await ValidateConfigAsync(dbOptions);

    // WX-171: warm the localized-template cache now the schema exists (the store loads in its
    // constructor on first resolution) and run the fail-closed startup completeness self-check.
    // The renderer reads every phrase from this cache now (ReportVocabulary is gone), so a load
    // failure is logged at ERROR — WxMonitor alerts — but is NOT fatal: the per-recipient send
    // gate is the actual stop, and an unrelated language must not block the whole service.
    try
    {
        var templates = host.Services.GetRequiredService<LanguageTemplateStore>();
        var loaded = templates.LoadedLanguages.OrderBy(c => c, StringComparer.Ordinal).ToList();
        Logger.Info($"Language templates loaded for: {string.Join(", ", loaded)}.");

        // Completeness self-check over ALL loaded languages, regardless of recipients (WX-171
        // fail-closed posture, layer 2): any language missing a HARD-required token (Tok.Required —
        // a missing SOFT cosmetic token is expected while top-up fills it and does NOT alert, WX-256)
        // logs an ERROR so it is screamed about and fixed even when unused. This blocks NOTHING —
        // it is pure alerting; the send-time per-recipient gate is what actually withholds a report.
        // WX-249: scope the completeness check to ENABLED languages. Disabled languages now keep
        // their curated templates (durable data — IsEnabled gates use, not existence) but are
        // dormant, so a dormant-but-incomplete language must not raise a false INCOMPLETE alert; the
        // per-recipient send gate already keys on IsEnabled. Key this set on the RAW IsoCode so it
        // compares on the same basis as `loaded` (the store keys ByIso on the un-canonicalized
        // IsoCode, and the whole system assumes IsoCode is already the canonical lower-case 2-letter
        // code); canonicalizing only one side would silently drop a regional-coded enabled language
        // out of this fail-closed check.
        HashSet<string> enabledIsos;
        using (var enabledCtx = new WeatherDataContext(dbOptions))
            enabledIsos = enabledCtx.Languages.Where(l => l.IsEnabled)
                .Select(l => l.IsoCode)
                .AsEnumerable()
                .ToHashSet(StringComparer.Ordinal);

        var checkedCount = 0;
        var incomplete = 0;
        foreach (var iso in loaded)
        {
            if (!enabledIsos.Contains(iso))
                continue;   // WX-249: dormant disabled language — its rows are durable, not alerted.
            checkedCount++;
            var missing = templates.MissingTokens(iso, Tok.Required);
            if (missing.Count == 0)
                continue;
            incomplete++;
            Logger.Error($"Language '{iso}' is INCOMPLETE — {missing.Count} renderer template token(s) missing " +
                $"([{string.Join(", ", missing.Take(15))}{(missing.Count > 15 ? ", …" : "")}]). " +
                "Recipients in this language will fail closed (no report) until repaired. (WX-171 startup completeness check.)");
        }
        if (incomplete == 0 && checkedCount > 0)
            Logger.Info($"Language template completeness check passed for all {checkedCount} enabled language(s).");
        else if (checkedCount == 0)
            Logger.Error("No enabled language has templates loaded at startup — every recipient will fail closed. (WX-171; check that a language is enabled and its templates are seeded/generated.)");
    }
    catch (Exception ex)
    {
        Logger.Error("Language template load/completeness check failed at startup; recipients fail closed until resolved.", ex);
    }

    await PrerequisiteChecker.LogPrerequisitesAsync(
        PrerequisiteChecker.Requires.SqlServer,
        connectionString: appConfig.GetConnectionString("WeatherData") ?? "");

    await host.RunAsync();
}
catch (Exception ex)
{
    Logger.Error("Fatal error during startup.", ex);
    throw;
}

// Loads AboutPaul.md from the service's binary directory. The file ships
// alongside the executable via the <Content> include in WxReport.Svc.csproj
// (source of truth at the repo root). A missing file is treated as a fatal
// startup error: the persona prefix is required for every Claude call, and
// silently falling back to generic-Claude output would be a worse failure
// mode than refusing to start.
static PersonaPrefix LoadPersonaPrefix()
{
    var path = Path.Combine(AppContext.BaseDirectory, "AboutPaul.md");
    if (!File.Exists(path))
    {
        Logger.Error($"AboutPaul.md not found at {path}. The persona prefix is required for WxReport.Svc to start.");
        throw new FileNotFoundException("AboutPaul.md not found", path);
    }
    string text;
    try
    {
        text = File.ReadAllText(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Logger.Error($"Failed to read AboutPaul.md at {path}. The persona prefix is required for WxReport.Svc to start.", ex);
        throw;
    }
    if (string.IsNullOrWhiteSpace(text))
    {
        Logger.Error($"AboutPaul.md at {path} is empty or whitespace-only. The persona prefix must have content for WxReport.Svc to start.");
        throw new InvalidOperationException($"AboutPaul.md at {path} is empty or whitespace-only.");
    }
    Logger.Info($"Loaded persona prefix from {path} ({text.Length} chars).");
    return new PersonaPrefix(text);
}

static async Task ValidateConfigAsync(DbContextOptions<WeatherDataContext> dbOptions)
{
    var issues = new List<string>();

    await using var ctx = new WeatherDataContext(dbOptions);
    var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);

    if (string.IsNullOrWhiteSpace(gs?.SmtpUsername)) issues.Add("SmtpUsername");
    if (string.IsNullOrWhiteSpace(gs?.SmtpPassword)) issues.Add("SmtpPassword");
    if (string.IsNullOrWhiteSpace(gs?.SmtpFromAddress)) issues.Add("SmtpFromAddress");
    if (string.IsNullOrWhiteSpace(gs?.ClaudeApiKey)) issues.Add("ClaudeApiKey");

    var recipientCount = await ctx.Recipients.CountAsync();
    if (recipientCount == 0) issues.Add("Recipients (none in database)");

    if (issues.Count > 0)
        Logger.Warn($"Missing required configuration — reports will not send until resolved: {string.Join(", ", issues)}. " +
                    "Use WxManager → Configure to set credentials.");
    else
        Logger.Info("Configuration validated.");

    // WX-235: the Gemini key gates only operator "Rerun QA" regenerations, not report sending — flag it
    // separately so a missing key is visible at startup without implying reports are blocked.
    if (string.IsNullOrWhiteSpace(gs?.GeminiApiKey))
        Logger.Warn("GlobalSettings.GeminiApiKey is not set — operator 'Rerun QA' regenerations (WX-235) will fail until it is configured; report sending is unaffected.");
}