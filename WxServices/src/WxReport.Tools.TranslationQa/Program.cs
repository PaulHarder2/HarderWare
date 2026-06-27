using System.Globalization;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxReport.Svc;
using WxReport.Tools.TranslationQa;

// WX-216 — live render harness. Drives the REAL report pipeline
// (ForecastReconciler → ClaudeClient → StructuredReportRenderer) against the WX-215 exemplar
// fixtures for English + a target language, and writes the rendered report HTML for the WX-214
// translation-QA judge to audit. Replicates the production wiring in WxReport.Svc/ReportWorker.
//
// Usage:
//   --lang <iso>        target language (required), e.g. de, es, eo, da
//   --scenario <name>   warm-convective | winter-frozen (default: both)
//   --out <dir>         output directory (default: C:\HarderWare\translation-qa)

var argMap = ParseArgs(args);
if (!argMap.TryGetValue("lang", out var targetIso) || string.IsNullOrWhiteSpace(targetIso))
{
    Console.Error.WriteLine("error: --lang <iso> is required (the target language to audit).");
    Console.Error.WriteLine("usage: --lang <iso> [--scenario warm-convective|winter-frozen] [--out <dir>]");
    return 2;
}
// Canonicalize to the bare ISO the template/render contract is keyed on (e.g. es-419 → es, DE → de),
// so MissingTokens, ForLanguage, CultureFor, narrativeLanguages, and the en-dedup all agree.
targetIso = LanguageTemplateStore.CanonicalIso(targetIso);
if (string.IsNullOrWhiteSpace(targetIso))
{
    Console.Error.WriteLine("error: --lang did not resolve to a language code.");
    return 2;
}
var outDir = argMap.TryGetValue("out", out var o) && !string.IsNullOrWhiteSpace(o)
    ? o
    : @"C:\HarderWare\translation-qa";

var scenarios = Exemplars.All();
if (argMap.TryGetValue("scenario", out var scn) && !string.IsNullOrWhiteSpace(scn))
{
    scenarios = [.. scenarios.Where(s => s.Name.Equals(scn.Trim(), StringComparison.OrdinalIgnoreCase))];
    if (scenarios.Count == 0)
    {
        Console.Error.WriteLine($"error: unknown --scenario '{scn}'. Known: warm-convective, winter-frozen.");
        return 2;
    }
}

// ── wiring (mirrors WxReport.Svc/Program.cs + ReportWorker) ───────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: false)
    .Build();

var connectionString = config.GetConnectionString("WeatherData");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("error: ConnectionStrings:WeatherData missing from appsettings.shared.json.");
    return 1;
}

var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
    .UseSqlServer(connectionString)
    .Options;

var claudeCfg = new ClaudeConfig();
config.GetSection("Claude").Bind(claudeCfg);
var reportCfg = new ReportConfig();
config.GetSection("Report").Bind(reportCfg);

// API key lives in the DB (GlobalSettings), exactly as the service reads it.
await using (var ctx = new WeatherDataContext(dbOptions))
{
    var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);
    claudeCfg.ApiKey = gs?.ClaudeApiKey;
}
if (string.IsNullOrWhiteSpace(claudeCfg.ApiKey))
{
    Console.Error.WriteLine("error: GlobalSettings.ClaudeApiKey is not set in the database — cannot make a live Claude call.");
    return 1;
}

// Persona prefix ships beside the binary (copied from AboutPaul.md), as in the service.
var personaPath = Path.Combine(AppContext.BaseDirectory, "AboutPaul.md");
if (!File.Exists(personaPath))
{
    Console.Error.WriteLine($"error: AboutPaul.md not found at {personaPath}.");
    return 1;
}
var persona = new PersonaPrefix(await File.ReadAllTextAsync(personaPath));

// DB-backed template store (the production path, not the test seed).
var templates = new LanguageTemplateStore(() =>
{
    using var ctx = new WeatherDataContext(dbOptions);
    return ctx.LanguageTemplates.Include(t => t.Language).AsNoTracking().ToList();
});

// Render languages: English reference + the target (deduped if target is en).
string[] renderLangs = targetIso == "en" ? ["en"] : ["en", targetIso];

// Fail early and clearly if the target language isn't generated/complete in the DB — the renderer
// fails loud on a missing token, so there's no point making a Claude call we can't render.
foreach (var lang in renderLangs)
{
    var missing = templates.MissingTokens(lang, Tok.All);
    if (missing.Count > 0)
    {
        Console.Error.WriteLine($"error: language '{lang}' is missing {missing.Count} template token(s) in the DB " +
            $"(e.g. {string.Join(", ", missing.Take(5))}). Enable + generate it before auditing.");
        return 1;
    }
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(claudeCfg.TimeoutSeconds) };
http.DefaultRequestHeaders.Add("User-Agent", "WxReport-TranslationQA/1.0");
var reconciler = new ForecastReconciler(new ClaudeClient(http, claudeCfg.ApiKey!, claudeCfg.Model, persona.Text), templates);

Directory.CreateDirectory(outDir);
var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var tz = Exemplars.LocalityTz;

// Unit prefs are language-neutral; give each render natural units (en→imperial, target→metric) so
// the judge isn't distracted by Fahrenheit in a German report. The same StructuredReport renders in
// either unit (the renderer formats the quantity tokens per recipient).
static Recipient RecipientFor(string lang) => lang == "en" ? Imperial() : Metric();

// Ctrl-C cancels the in-flight Claude call cleanly (the HttpClient timeout is the hard backstop).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var anyFailed = false;
foreach (var scenario in scenarios)
{
    Console.WriteLine($"\n=== {scenario.Name} — {scenario.Synopsis} ===");
    Console.WriteLine($"Reconciling (en + {targetIso}) via a live Claude call …");

    var prior = new ForecastSnapshot
    {
        StationIcao = scenario.PrimaryObservation.StationIcao,
        GeneratedAtUtc = scenario.AnchorDay.AddHours(-6),
        SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
        Body = scenario.Prior.Serialize(),
    };

    ReconcileResult result;
    try
    {
        result = await reconciler.ReconcileAsync(
            scenario.PrimaryObservation,
            scenario.Provisional,
            gfsModelRunUtc: scenario.AnchorDay.AddHours(-6),
            tafIssuanceUtc: null,
            tafValidToUtc: null,
            prior: prior,
            narrativeLanguages: renderLangs,
            tz: tz,
            reportKind: ReportKind.Diagnostic,
            allowSkip: false,
            changedSinceLastSend: Array.Empty<TriggerSource>(),
            significanceCfg: reportCfg.SignificanceGate,
            nowUtc: scenario.AnchorDay,
            ct: cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nCancelled.");
        return 130; // conventional 128 + SIGINT
    }

    if (result is not ReconcileResult.Success success)
    {
        anyFailed = true;
        var detail = result switch
        {
            ReconcileResult.Degraded d => $"Degraded — {d.Reason}",
            ReconcileResult.Failure f => $"Failure — {f.Reason}",
            ReconcileResult.NotNews n => $"NotNews — {n.ReasoningTrace}",
            _ => result.GetType().Name,
        };
        Console.Error.WriteLine($"  ✗ reconcile did not succeed: {detail}");
        Console.Error.WriteLine("    No artifact written (a non-Success result would render a misleading report).");
        continue;
    }

    Console.WriteLine($"  ✓ reconciled. Tokens: {success.Tokens}");

    foreach (var lang in renderLangs)
    {
        var html = StructuredReportRenderer.Render(
            success.StructuredReport,
            success.FinalSnapshot,
            scenario.PrimaryObservation,
            RecipientFor(lang),
            templates.ForLanguage(lang),
            templates.CultureFor(lang),
            tz,
            ReportKind.Diagnostic,
            scenario.AnchorDay);

        var path = Path.Combine(outDir, $"{scenario.Name}.{lang}.{stamp}.html");
        await File.WriteAllTextAsync(path, html);
        Console.WriteLine($"  → {(lang == "en" ? "reference" : "target  ")} [{lang}]  {path}");
    }
}

Console.WriteLine();
return anyFailed ? 1 : 0;

// ── helpers ──────────────────────────────────────────────────────────────────────────────────

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
            continue;
        var key = args[i][2..];
        var val = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
        map[key] = val;
    }
    return map;
}

static Recipient Imperial() => new()
{
    RecipientId = "qa-en",
    Name = "QA",
    Email = "qa@example.com",
    TempUnit = "F",
    PressureUnit = "inHg",
    WindSpeedUnit = "mph",
    PrecipUnit = "in",
};

static Recipient Metric() => new()
{
    RecipientId = "qa-target",
    Name = "QA",
    Email = "qa@example.com",
    TempUnit = "C",
    PressureUnit = "kPa",
    WindSpeedUnit = "kph",
    PrecipUnit = "mm",
};