using System.Globalization;
using System.Text.Json;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxReport.Svc;
using WxReport.Tools.TranslationQa;

// WX-214 translation-QA harness. Two phases:
//
//   Phase 1 — GENERATE (WX-216/217): drive the REAL report pipeline (ForecastReconciler →
//   ClaudeClient → StructuredReportRenderer) against the WX-215 fixtures for English + a target
//   language; write the rendered HTML, the judging request (.request.md), the structured request
//   (.request.json), and a pre-created paste target (.response.txt). Replicates WxReport.Svc/ReportWorker.
//   With --judge gemini (WX-227), it also judges automatically in the same run via the Gemini API and
//   writes <iso>.<stamp>.judged.json — no paste, no chunks, full audit.
//
//   Phase 2 — JUDGE (WX-218): given --response <file> (a reply the operator pasted back from a
//   non-Claude model by hand), parse + validate it into a JudgeResponse and write the judged.json.
//   No DB, no Claude. This is the manual fallback to --judge gemini.
//
// Usage:
//   --lang <iso>        target language (required), e.g. de, es, eo, da
//   --scenario <name>   warm-convective | winter-frozen (default: both)   [generate]
//   --out <dir>         output directory (default: C:\HarderWare\translation-qa)   [generate]
//   --judge gemini      after generating, judge automatically via the Gemini API   [generate]
//   --response <file>   parse a saved model reply instead of generating (manual fallback)   [judge]

var argMap = ParseArgs(args);

// Ctrl-C cancels cleanly in either phase (the HttpClient timeout is the generate phase's hard backstop).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// ── PHASE 2 — judge: parse a saved model reply into a validated JudgeResponse. Independent of --lang,
// the DB, and Claude (the verdict's language comes from the reply), so handle it before the
// generate-only --lang requirement. ──
if (argMap.TryGetValue("response", out var responseFile) && !string.IsNullOrWhiteSpace(responseFile))
    return await RunJudgePhaseAsync(responseFile, cts.Token);

// ── PHASE 1 — generate (requires --lang) ─────────────────────────────────────────────────────────
if (!argMap.TryGetValue("lang", out var targetIso) || string.IsNullOrWhiteSpace(targetIso))
{
    Console.Error.WriteLine("error: --lang <iso> is required to generate (or pass --response <reply-file> to parse a reply).");
    Console.Error.WriteLine("usage: --lang <iso> [--scenario warm-convective|winter-frozen] [--out <dir>]  |  --response <reply-file>");
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

// WX-227: optional automated judge. Validate early so a typo fails before the expensive Claude calls.
var judgeProvider = argMap.GetValueOrDefault("judge");
if (!string.IsNullOrWhiteSpace(judgeProvider) && !string.Equals(judgeProvider, "gemini", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"error: unknown --judge provider '{judgeProvider}'. Supported: gemini.");
    return 2;
}
var autoJudgeGemini = string.Equals(judgeProvider, "gemini", StringComparison.OrdinalIgnoreCase);

// ── wiring (mirrors WxReport.Svc/Program.cs + ReportWorker) ───────────────────────────────────
// shared config beside the binary + the tool-local overlay at the InstallRoot, where the Gemini API
// key lives (Option B — a dev-tool secret, never in the shared file or the DB).
var sharedConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.shared.json", optional: false, reloadOnChange: false)
    .Build();
var installRoot = sharedConfig["InstallRoot"];
if (string.IsNullOrWhiteSpace(installRoot))
{
    // Resolve the overlay strictly from InstallRoot — never fall back to the binary dir, which would
    // silently read a different appsettings.local.json (bin\… under dotnet run) and mask a config error.
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

// WX-227: when auto-judging, bind the Gemini config + fail fast on a missing key, before the Claude calls.
GeminiConfig? geminiCfg = null;
if (autoJudgeGemini)
{
    geminiCfg = new GeminiConfig();
    config.GetSection("Gemini").Bind(geminiCfg);
    if (string.IsNullOrWhiteSpace(geminiCfg.ApiKey))
    {
        Console.Error.WriteLine($"error: --judge gemini, but Gemini:ApiKey is not set in appsettings.local.json at {installRoot ?? "the InstallRoot"}.");
        return 1;
    }
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

var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
// WX-232: each check's files live in their own per-check subfolder "<iso>.<stamp>", named
// "<lang>.<stamp>.<purpose>.<ext>" within it (purpose = a scenario name, request, response, judged).
var packageDir = Path.Combine(outDir, $"{targetIso}.{stamp}");
try
{
    Directory.CreateDirectory(packageDir); // also creates outDir as its parent
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"error: could not create the output directory '{packageDir}' — {ex.Message}");
    return 1;
}
var tz = Exemplars.LocalityTz;

// Unit prefs are language-neutral; give each render natural units (en→imperial, target→metric) so
// the judge isn't distracted by Fahrenheit in a German report. The same StructuredReport renders in
// either unit (the renderer formats the quantity tokens per recipient).
static Recipient RecipientFor(string lang) => lang == "en" ? Imperial() : Metric();

var anyFailed = false;
var rendered = new List<RenderedScenario>(); // WX-217: collected for the per-language judging request
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

    var htmlByLang = new Dictionary<string, string>(StringComparer.Ordinal);
    try
    {
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
            htmlByLang[lang] = html;

            var path = Path.Combine(packageDir, $"{lang}.{stamp}.{scenario.Name}.html");
            await File.WriteAllTextAsync(path, html, cts.Token);
            Console.WriteLine($"  → {(lang == "en" ? "reference" : "target  ")} [{lang}]  {path}");
        }
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nCancelled.");
        return 130;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: could not write the rendered report — {ex.Message}");
        return 1;
    }

    rendered.Add(new RenderedScenario(
        scenario.Name,
        scenario.Synopsis,
        htmlByLang.GetValueOrDefault("en", ""),
        htmlByLang.GetValueOrDefault(targetIso, htmlByLang.GetValueOrDefault("en", ""))));
}

// WX-217: assemble one judging request per language from the rendered reports + the paired vocabulary.
if (targetIso == "en")
{
    Console.WriteLine("\n(no judging request: --lang en is the reference language, nothing to audit.)");
}
else if (rendered.Count > 0)
{
    List<VocabularyPair> vocabulary;
    string? targetDisplayName;
    await using (var ctx = new WeatherDataContext(dbOptions))
    {
        var enRows = await ctx.LanguageTemplates.Include(t => t.Language)
            .Where(t => t.Language!.IsoCode == "en").AsNoTracking().ToListAsync(cts.Token);
        var tgtRows = await ctx.LanguageTemplates.Include(t => t.Language)
            .Where(t => t.Language!.IsoCode == targetIso).AsNoTracking().ToListAsync(cts.Token);
        targetDisplayName = tgtRows.FirstOrDefault()?.Language?.DisplayName;
        var tgtByToken = tgtRows.ToDictionary(t => t.Token, StringComparer.Ordinal);
        vocabulary = enRows.OrderBy(e => e.Token, StringComparer.Ordinal).Select(e =>
        {
            tgtByToken.TryGetValue(e.Token, out var t);
            return new VocabularyPair(
                e.Token, e.Phrase, e.ContextInfo, e.ContextKind.ToString(),
                t?.Phrase ?? "", t?.ContextInfo ?? "", t?.Representable ?? false, t?.Note,
                Reviewed: t?.ReviewedBy is not null);
        }).ToList();
    }

    var requestMarkdown = JudgingPayload.Build(targetIso, targetDisplayName, rendered, vocabulary);
    var requestPath = Path.Combine(packageDir, $"{targetIso}.{stamp}.request.md");

    // Persist the structured request so the judge/artifact phases never re-reconcile (Claude is
    // non-deterministic — the artifact must show exactly what the judge saw). WX-219 consumes this.
    var requestJsonPath = Path.Combine(packageDir, $"{targetIso}.{stamp}.request.json");
    var requestObj = new JudgingRequest(targetIso, targetDisplayName, rendered, vocabulary);
    try
    {
        await File.WriteAllTextAsync(requestPath, requestMarkdown, cts.Token);
        await File.WriteAllTextAsync(requestJsonPath, JsonSerializer.Serialize(requestObj, TranslationQaJson.Write), cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\nCancelled.");
        return 130;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"error: could not write the judging request artifacts — {ex.Message}");
        return 1;
    }
    Console.WriteLine($"\n  → judging request  {requestPath}");

    if (autoJudgeGemini)
    {
        // WX-227: judge automatically via the Gemini API in this same run — the full request goes in one
        // call and the full verdict comes back (no paste limit, no chunks).
        Console.WriteLine($"Judging (en + {targetIso}) via the Gemini API ({geminiCfg!.Model}) …");
        using var geminiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(geminiCfg.TimeoutSeconds) };
        IJudge judge = new GeminiJudge(geminiHttp, geminiCfg);
        try
        {
            var verdict = (await judge.JudgeAsync(requestMarkdown, cts.Token))
                with
            { JudgedBy = $"gemini ({geminiCfg.Model})" }; // WX-219: stamp the package source
            var judgedPath = Path.Combine(packageDir, $"{targetIso}.{stamp}.judged.json");
            await File.WriteAllTextAsync(judgedPath, JsonSerializer.Serialize(verdict, TranslationQaJson.Write), cts.Token);
            Console.WriteLine("  ✓ judged by Gemini.");
            PrintVerdictSummary(verdict);
            Console.WriteLine($"\n  → verdict  {judgedPath}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("\nCancelled.");
            return 130;
        }
        catch (OperationCanceledException)
        {
            // HttpClient's own timeout surfaces as a TaskCanceledException whose token is NOT cts.
            Console.Error.WriteLine($"error: the Gemini call timed out after {geminiCfg.TimeoutSeconds}s (raise Gemini:TimeoutSeconds for a large audit).");
            return 1;
        }
        catch (JudgeParseException ex)
        {
            Console.Error.WriteLine($"error: Gemini judge failed — {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: could not write the verdict file — {ex.Message}");
            return 1;
        }
    }
    else
    {
        // Manual fallback (WX-218): pre-create the response paste target + print the cue card.
        var responseTxtPath = Path.Combine(packageDir, $"{targetIso}.{stamp}.response.txt");
        var rerun = $"dotnet run --project src\\WxReport.Tools.TranslationQa -- --response \"{responseTxtPath}\"";
        try
        {
            await File.WriteAllTextAsync(responseTxtPath,
                "Paste the FULL reply from Copilot/ChatGPT here (replace this text), then save and close.\r\n" +
                "Then run:\r\n  " + rerun + "\r\n", cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("\nCancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: could not write the response paste target — {ex.Message}");
            return 1;
        }

        Console.WriteLine("\nNext steps:");
        Console.WriteLine("  1. Open the request and paste it into Copilot or ChatGPT (a non-Claude model):");
        Console.WriteLine($"       {requestPath}");
        Console.WriteLine("  2. Paste the reply into this file (created for you) and save:");
        Console.WriteLine($"       {responseTxtPath}");
        Console.WriteLine("  3. Parse the reply:");
        Console.WriteLine($"       {rerun}");
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
        { JudgedBy = "manual-paste" }; // WX-219: stamp the package source
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

// Shared one-glance verdict summary (used by the manual parse phase and the Gemini auto-judge).
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