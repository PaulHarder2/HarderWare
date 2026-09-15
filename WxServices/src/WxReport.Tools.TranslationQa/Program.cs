using System.Text.Json;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxReport.Svc;
using WxReport.Svc.TranslationQa;
using WxReport.Tools.TranslationQa;

// WX-214 translation-QA harness. Two phases:
//
//   Phase 1 — GENERATE (WX-216/217): drive the REAL report pipeline (ForecastReconciler →
//   ClaudeClient → StructuredReportRenderer) against the WX-215 fixtures for English + a target
//   language, write the rendered HTML + the judging request, and — with --judge gemini (WX-227) —
//   judge automatically and write <iso>.<stamp>.judged.json. The pipeline now lives in the shared
//   WxReport.Svc TranslationQaRunner (WX-235), so the service can regenerate packages too; this tool
//   builds the dependencies from config and calls it.
//
//   Phase 2 — JUDGE (WX-218): given --response <file> (a reply the operator pasted back from a
//   non-Claude model by hand), parse + validate it into a JudgeResponse and write the judged.json.
//   No DB, no Claude. This is the manual fallback to --judge gemini.
//
// Usage:
//   --lang <iso>        target language (required), e.g. de, es, eo, da
//   --scenario <name>   warm-convective | winter-frozen (default: both), or the harness-only
//                       chicago-day-parts (WX-504; never in the default, never in QA reruns)   [generate]
//   --raw               also write each reconciled scenario's snapshot + structured report as JSON   [generate]
//   --out <dir>         output directory (default: C:\HarderWare\translation-qa)   [generate]
//   --judge gemini      after generating, judge automatically via the Gemini API   [generate]
//   --response <file>   parse a saved model reply instead of generating (manual fallback)   [judge]
//
// WX-506 replay mode (no QA package; prints to stdout):
//   --replay-band --prior <ForecastSnapshotId> --final <ForecastSnapshotId> --tz <IANA id> --langs en,es [--now <ISO UTC>]
//                       --now defaults to the final snapshot's GeneratedAtUtc, the stored instant closest to the cycle.
//                       re-write the "Why this update" band from two stored snapshots through the production
//                       band path, printing the computed facts and the band per language.
//                       Exit 0 band written · 1 band call failed or rejected · 2 usage or unreadable snapshot · 3 no computed changes
//   --replay-band ... --facts-only
//                       print only the computed facts the band call would receive; no model call.
//                       Exit 0 facts printed · 2 usage · 3 no computed changes

var argMap = ParseArgs(args);

// Ctrl-C cancels cleanly in either phase (the HttpClient timeout is the generate phase's hard backstop).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// ── PHASE 2 — judge: parse a saved model reply into a validated JudgeResponse. Independent of --lang,
// the DB, and Claude, so handle it before the generate-only --lang requirement. ──
if (argMap.TryGetValue("response", out var responseFile) && !string.IsNullOrWhiteSpace(responseFile))
    return await RunJudgePhaseAsync(responseFile, cts.Token);

var replayBand = argMap.ContainsKey("replay-band");

// ── PHASE 1 — generate (requires --lang) ─────────────────────────────────────────────────────────
string? targetIso = null;
if (!replayBand && (!argMap.TryGetValue("lang", out targetIso) || string.IsNullOrWhiteSpace(targetIso)))
{
    Console.Error.WriteLine("error: --lang <iso> is required to generate (or pass --response <reply-file> to parse a reply).");
    Console.Error.WriteLine("usage: --lang <iso> [--scenario warm-convective|winter-frozen|chicago-day-parts] [--out <dir>] [--judge gemini] [--raw]  |  --response <reply-file>");
    return 2;
}
targetIso = replayBand ? "" : LanguageTemplateStore.CanonicalIso(targetIso!);
if (!replayBand && string.IsNullOrWhiteSpace(targetIso))
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
    // WX-504: resolve across All() and the harness-only scenarios. The default above stays All().
    var named = Exemplars.Named(scn);
    if (named is null)
    {
        var known = string.Join(", ", Exemplars.All().Concat(Exemplars.HarnessOnly()).Select(s => s.Name));
        Console.Error.WriteLine($"error: unknown --scenario '{scn}'. Known: {known}.");
        return 2;
    }
    scenarios = [named];
}

// WX-227: optional automated judge. Validate early so a typo fails before the expensive Claude calls.
var judgeProvider = argMap.GetValueOrDefault("judge");
if (!string.IsNullOrWhiteSpace(judgeProvider) && !string.Equals(judgeProvider, "gemini", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"error: unknown --judge provider '{judgeProvider}'. Supported: gemini.");
    return 2;
}
var autoJudgeGemini = string.Equals(judgeProvider, "gemini", StringComparison.OrdinalIgnoreCase);

// ── wiring (mirrors WxReport.Svc/Program.cs) ──────────────────────────────────────────────────
var sharedConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: false)
    .Build();
var installRoot = sharedConfig["InstallRoot"];
if (string.IsNullOrWhiteSpace(installRoot))
{
    Console.Error.WriteLine("error: InstallRoot missing from appsettings.shared.json.");
    return 1;
}
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: false)
    .AddJsonFile(Path.Combine(installRoot, "appsettings.local.json"), optional: true, reloadOnChange: false)
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

// Both API keys live in the DB (GlobalSettings), exactly as the service reads them (WX-235 single
// source of truth — the Gemini key moved out of appsettings.local.json into the DB).
string? geminiKeyFromDb;
await using (var ctx = new WeatherDataContext(dbOptions))
{
    var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1);
    claudeCfg.ApiKey = gs?.ClaudeApiKey;
    geminiKeyFromDb = gs?.GeminiApiKey;
}
// --replay-band --facts-only makes no model call, so it needs neither the Claude key nor the persona.
var factsOnly = replayBand && argMap.ContainsKey("facts-only");
if (!factsOnly && string.IsNullOrWhiteSpace(claudeCfg.ApiKey))
{
    Console.Error.WriteLine("error: GlobalSettings.ClaudeApiKey is not set in the database — cannot make a live Claude call.");
    return 1;
}

// WX-227: when auto-judging, bind the Gemini config (model/timeout overrides) + take the key from the DB;
// fail fast on a missing key before the expensive Claude calls.
GeminiConfig? geminiCfg = null;
if (autoJudgeGemini)
{
    geminiCfg = new GeminiConfig();
    config.GetSection("Gemini").Bind(geminiCfg);   // optional model/timeout/base-url overrides
    geminiCfg.ApiKey = geminiKeyFromDb;            // WX-235: key from GlobalSettings, not appsettings
    if (string.IsNullOrWhiteSpace(geminiCfg.ApiKey))
    {
        Console.Error.WriteLine("error: --judge gemini, but GlobalSettings.GeminiApiKey is not set in the database.");
        return 1;
    }
}

// Persona prefix ships beside the binary (copied from AboutPaul.md), as in the service.
var personaPath = Path.Combine(AppContext.BaseDirectory, "AboutPaul.md");
if (!factsOnly && !File.Exists(personaPath))
{
    Console.Error.WriteLine($"error: AboutPaul.md not found at {personaPath}.");
    return 1;
}
var persona = new PersonaPrefix(File.Exists(personaPath) ? await File.ReadAllTextAsync(personaPath) : "");

// DB-backed template store (the production path, not the test seed).
var templates = new LanguageTemplateStore(
    () =>
    {
        using var ctx = new WeatherDataContext(dbOptions);
        return ctx.LanguageTemplates.Include(t => t.Language).AsNoTracking().ToList();
    },
    () =>   // WX-238: same prompt-glossary tokens as the service, so the QA re-run exercises the fix.
    {
        using var ctx = new WeatherDataContext(dbOptions);
        return ctx.PromptGlossaryTokens.AsNoTracking().Select(g => g.Token).ToList();
    });

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(claudeCfg.TimeoutSeconds) };
http.DefaultRequestHeaders.Add("User-Agent", "WxReport-TranslationQA/1.0");
var reconciler = new ForecastReconciler(new ClaudeClient(http, claudeCfg.ApiKey ?? "", claudeCfg.Model, persona.Text), templates);

if (replayBand)
{
    try
    {
        return await RunReplayBandAsync(argMap, reconciler, reportCfg, dbOptions, cts.Token);
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.Error.WriteLine("\nCancelled.");
        return 130;
    }
}

HttpClient? geminiHttp = null;
IJudge? judge = null;
if (autoJudgeGemini)
{
    geminiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(geminiCfg!.TimeoutSeconds) };
    judge = new GeminiJudge(geminiHttp, geminiCfg);
}

TranslationQaRunner.Result result;
try
{
    result = await TranslationQaRunner.RunAsync(
        targetIso, scenarios, outDir, reconciler, templates, reportCfg,
        () => new WeatherDataContext(dbOptions), judge, Console.WriteLine, cts.Token,
        writeReconciledJson: argMap.ContainsKey("raw"));
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.Error.WriteLine("\nCancelled.");
    return 130; // conventional 128 + SIGINT
}
catch (OperationCanceledException)
{
    // A Claude/Gemini HttpClient self-timeout surfaces as a TaskCanceledException whose token is NOT cts.
    Console.Error.WriteLine("error: a live model call timed out — raise Claude:TimeoutSeconds / Gemini:TimeoutSeconds for a large audit.");
    return 1;
}
catch (JudgeParseException ex)
{
    Console.Error.WriteLine($"error: judge failed — {ex.Message}");
    return 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
finally
{
    geminiHttp?.Dispose();
}

if (result.TargetIso == "en")
{
    Console.WriteLine("\n(no judging request: --lang en is the reference language, nothing to audit.)");
}
else if (autoJudgeGemini && result.Judged)
{
    Console.WriteLine($"\n  ✓ judged by Gemini → {result.PackageDir}");
}
else if (result.RequestPath is not null)
{
    // Manual fallback (WX-218): pre-create the response paste target + print the cue card.
    var responseTxtPath = Path.Combine(result.PackageDir, $"{result.TargetIso}.{result.Stamp}.response.txt");
    var rerun = $"dotnet run --project src\\WxReport.Tools.TranslationQa -- --response \"{responseTxtPath}\"";
    try
    {
        await File.WriteAllTextAsync(responseTxtPath,
            "Paste the FULL reply from Copilot/ChatGPT here (replace this text), then save and close.\r\n" +
            "Then run:\r\n  " + rerun + "\r\n", cts.Token);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: could not write the response paste target — {ex.Message}");
        return 1;
    }

    Console.WriteLine("\nNext steps:");
    Console.WriteLine("  1. Open the request and paste it into Copilot or ChatGPT (a non-Claude model):");
    Console.WriteLine($"       {result.RequestPath}");
    Console.WriteLine("  2. Paste the reply into this file (created for you) and save:");
    Console.WriteLine($"       {responseTxtPath}");
    Console.WriteLine("  3. Parse the reply:");
    Console.WriteLine($"       {rerun}");
}

Console.WriteLine();
return result.AnyScenarioFailed ? 1 : 0;

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

// WX-506: re-write a "Why this update" band from two stored snapshots through the production band path.
static async Task<int> RunReplayBandAsync(
    Dictionary<string, string> argMap, ForecastReconciler reconciler, ReportConfig reportCfg,
    DbContextOptions<WeatherDataContext> dbOptions, CancellationToken ct)
{
    if (!int.TryParse(argMap.GetValueOrDefault("prior"), out var priorId)
        || !int.TryParse(argMap.GetValueOrDefault("final"), out var finalId)
        || string.IsNullOrWhiteSpace(argMap.GetValueOrDefault("tz"))
        || string.IsNullOrWhiteSpace(argMap.GetValueOrDefault("langs"))
        || (argMap.ContainsKey("now") && !DateTime.TryParse(argMap["now"], System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out _)))
    {
        Console.Error.WriteLine("usage: --replay-band --prior <ForecastSnapshotId> --final <ForecastSnapshotId> --tz <IANA id> --langs en,es [--now <ISO UTC>] [--facts-only]");
        return 2;
    }

    var langs = argMap["langs"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(LanguageTemplateStore.CanonicalIso).ToArray();
    if (langs.Any(string.IsNullOrWhiteSpace))
    {
        Console.Error.WriteLine($"error: --langs '{argMap["langs"]}' contains a code that does not resolve to a language.");
        return 2;
    }
    langs = langs.Distinct(StringComparer.Ordinal).ToArray();

    TimeZoneInfo tz;
    try { tz = TimeZoneInfo.FindSystemTimeZoneById(argMap["tz"]); }
    catch (TimeZoneNotFoundException)
    {
        Console.Error.WriteLine($"error: --tz '{argMap["tz"]}' is not a known time zone.");
        return 2;
    }

    string? priorBody, finalBody;
    DateTime finalGeneratedUtc = default;
    await using (var ctx = new WeatherDataContext(dbOptions))
    {
        priorBody = await ctx.ForecastSnapshots.Where(s => s.Id == priorId).Select(s => s.Body).SingleOrDefaultAsync(ct);
        var final = await ctx.ForecastSnapshots.Where(s => s.Id == finalId)
            .Select(s => new { s.Body, s.GeneratedAtUtc }).SingleOrDefaultAsync(ct);
        finalBody = final?.Body;
        if (final is not null)
            finalGeneratedUtc = DateTime.SpecifyKind(final.GeneratedAtUtc, DateTimeKind.Utc);
    }
    if (priorBody is null || finalBody is null)
    {
        Console.Error.WriteLine($"error: ForecastSnapshot {(priorBody is null ? priorId : finalId)} does not exist.");
        return 2;
    }
    // A stored body can predate the current schema or be corrupt; say which snapshot, rather than crash.
    ForecastSnapshotBody priorSnapshot, finalSnapshot;
    try { priorSnapshot = ForecastSnapshotBody.Deserialize(priorBody); }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"error: ForecastSnapshot {priorId} body could not be parsed — {ex.Message}");
        return 2;
    }
    try { finalSnapshot = ForecastSnapshotBody.Deserialize(finalBody); }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"error: ForecastSnapshot {finalId} body could not be parsed — {ex.Message}");
        return 2;
    }
    // Without --now, the cycle instant is taken as the final snapshot's generation time: the closest stored instant
    // to when the cycle's changes were computed (a little after it, never before).
    var nowUtc = argMap.ContainsKey("now")
        ? DateTime.Parse(argMap["now"], System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        : finalGeneratedUtc;
    Console.WriteLine($"now: {nowUtc:yyyy-MM-ddTHH:mm:ssZ}");
    if (argMap.ContainsKey("facts-only"))
    {
        var facts = reconciler.BuildChangeBandFacts(
            priorSnapshot, finalSnapshot,
            langs, tz, reportCfg.SignificanceGate, nowUtc);
        if (facts.Changes.Count == 0)
        {
            Console.WriteLine("no computed changes: production would show no band and make no band call.");
            return 3;
        }
        Console.WriteLine(facts.UserMessage);
        return 0;
    }

    var replay = await reconciler.ReplayChangeBandAsync(
        priorSnapshot, finalSnapshot,
        langs, tz, reportCfg.SignificanceGate, nowUtc, ct);

    if (replay.Changes.Count == 0)
    {
        // Production makes no band call when nothing changed, so there is nothing to replay.
        Console.WriteLine("no computed changes: production would show no band and make no band call.");
        return 3;
    }

    Console.WriteLine(replay.UserMessage);
    Console.WriteLine("── change band ──");
    foreach (var (lang, band) in replay.ChangeSummary)
        Console.WriteLine($"{lang}: {band ?? "(null — the band call failed or was rejected; production would show the deterministic band)"}");
    Console.WriteLine($"tokens: in={replay.Tokens.InputTokens} out={replay.Tokens.OutputTokens} cache-read={replay.Tokens.CacheReadInputTokens} cache-write={replay.Tokens.CacheCreationInputTokens}");
    return replay.ChangeSummary.Values.All(v => v is not null) ? 0 : 1;
}

// Phase 2: parse the operator's saved model reply into a validated JudgeResponse and persist it.
static async Task<int> RunJudgePhaseAsync(string responseFile, CancellationToken ct)
{
    if (!File.Exists(responseFile))
    {
        Console.Error.WriteLine($"error: response file not found: {responseFile}");
        return 1;
    }

    IJudge judge = new ManualPasteJudge(responseFile);
    JudgeResponse verdict;
    try
    {
        // The manual judge ignores the request markdown (it was pasted into the model by hand).
        verdict = (await judge.JudgeAsync(string.Empty, ct))
            with
        { JudgedBy = judge.SourceLabel }; // WX-219: stamp the package source
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        Console.Error.WriteLine("\nCancelled.");
        return 130;
    }
    catch (JudgeParseException ex)
    {
        Console.Error.WriteLine($"error: couldn't parse the reply — {ex.Message}");
        Console.Error.WriteLine("Open the response file, paste the model's full JSON reply, save, and re-run.");
        return 1;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: couldn't read the response file '{responseFile}' — {ex.Message}");
        return 1;
    }

    var judgedPath = JudgedPathFor(responseFile);
    try
    {
        await File.WriteAllTextAsync(judgedPath, JsonSerializer.Serialize(verdict, TranslationQaJson.Write), ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        Console.Error.WriteLine("\nCancelled.");
        return 130;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: parsed the reply but couldn't write '{judgedPath}' — {ex.Message}");
        return 1;
    }

    Console.WriteLine($"Parsed verdict for '{verdict.Language}'.");
    PrintVerdictSummary(verdict);
    Console.WriteLine($"\n  → parsed verdict  {judgedPath}");
    return 0;
}

// One-glance verdict summary (the manual parse phase).
static void PrintVerdictSummary(JudgeResponse verdict)
{
    var conf = verdict.SelfReportedConfidence;
    Console.WriteLine($"  confidence:          {(conf is null ? "(none reported)" : $"{conf.Level} — {conf.Note}")}");
    Console.WriteLine($"  back-translations:   {verdict.BackTranslations.Count}");
    Console.WriteLine($"  report findings:     {verdict.ReportFindings.Count}");
    Console.WriteLine($"  vocabulary verdicts: {verdict.VocabularyVerdicts.Count}");
}

// Name the parsed-verdict file from the response file: <iso>.<stamp>.response.txt → <iso>.<stamp>.judged.json
static string JudgedPathFor(string responseFile)
{
    const string suffix = ".response.txt";
    return responseFile.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
        ? string.Concat(responseFile.AsSpan(0, responseFile.Length - suffix.Length), ".judged.json")
        : responseFile + ".judged.json";
}