using System.Globalization;
using System.Text.Json;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Common.TranslationQa;

namespace WxReport.Svc.TranslationQa;

/// <summary>
/// WX-235 — the reusable translation-QA generation pipeline, extracted from the
/// WxReport.Tools.TranslationQa console tool so both the tool (Phase 1) and the WxReport.Svc
/// <see cref="QaRerunWorker"/> can produce a fresh judge package. Per scenario it drives the real report
/// pipeline (<see cref="ForecastReconciler"/> → <see cref="ClaudeClient"/> → <see cref="StructuredReportRenderer"/>)
/// for English + the target language, writes the rendered HTML, the judging request (.request.md/.request.json),
/// and — when an <see cref="IJudge"/> is supplied — the judged verdict (.judged.json), all in the WX-232
/// per-check <c>&lt;iso&gt;.&lt;stamp&gt;</c> subfolder layout. Pure orchestration over injected dependencies:
/// the caller owns config, the Claude/Gemini clients, and (for the manual fallback) the no-judge path.
/// </summary>
public static class TranslationQaRunner
{
    /// <summary>Outcome of a generation run.</summary>
    public sealed record Result(
        string TargetIso,
        string Stamp,
        string PackageDir,
        int ScenariosRendered,
        bool AnyScenarioFailed,
        string? RequestMarkdown,
        string? RequestPath,
        bool Judged);

    /// <summary>
    /// Generate a translation-QA package for <paramref name="targetIso"/>. Renders every scenario in
    /// <paramref name="scenarios"/> for English + the target via <paramref name="reconciler"/>, writes the
    /// package under <paramref name="outDir"/>, and — when <paramref name="judge"/> is non-null — judges it
    /// into <c>&lt;iso&gt;.&lt;stamp&gt;.judged.json</c> (the only file that makes the package visible in the
    /// WX-219 review tab). Throws on a hard failure (missing templates, IO, judge error);
    /// <see cref="OperationCanceledException"/> propagates for the caller to map to a cancel.
    /// </summary>
    public static async Task<Result> RunAsync(
        string targetIso,
        IReadOnlyList<Exemplars.Scenario> scenarios,
        string outDir,
        ForecastReconciler reconciler,
        LanguageTemplateStore templates,
        ReportConfig reportCfg,
        Func<WeatherDataContext> dbFactory,
        IJudge? judge,
        Action<string>? log,
        CancellationToken ct)
    {
        void Log(string m) => log?.Invoke(m);

        // Canonicalize to the bare ISO the template/render contract is keyed on (e.g. es-419 → es, DE → de).
        targetIso = LanguageTemplateStore.CanonicalIso(targetIso);
        if (string.IsNullOrWhiteSpace(targetIso))
            throw new ArgumentException("target language did not resolve to a language code.", nameof(targetIso));

        // Render languages: English reference + the target (deduped if target is en).
        string[] renderLangs = targetIso == "en" ? ["en"] : ["en", targetIso];

        // Fail early if a language isn't generated/complete in the DB — the renderer fails loud on a missing
        // token, so there's no point making a Claude call we can't render.
        foreach (var lang in renderLangs)
        {
            var missing = templates.MissingTokens(lang, Tok.All);
            if (missing.Count > 0)
                throw new InvalidOperationException(
                    $"language '{lang}' is missing {missing.Count} template token(s) in the DB " +
                    $"(e.g. {string.Join(", ", missing.Take(5))}). Enable + generate it before auditing.");
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        // WX-232: each check's files live in their own per-check subfolder "<iso>.<stamp>".
        var packageDir = Path.Combine(outDir, $"{targetIso}.{stamp}");
        Directory.CreateDirectory(packageDir); // also creates outDir as its parent
        var tz = Exemplars.LocalityTz;

        var anyFailed = false;
        var rendered = new List<RenderedScenario>();
        foreach (var scenario in scenarios)
        {
            Log($"Reconciling {scenario.Name} (en + {targetIso}) via a live Claude call …");

            var prior = new ForecastSnapshot
            {
                StationIcao = scenario.PrimaryObservation.StationIcao,
                GeneratedAtUtc = scenario.AnchorDay.AddHours(-6),
                SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
                Body = scenario.Prior.Serialize(),
            };

            var result = await reconciler.ReconcileAsync(
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
                ct: ct);

            if (result is not ReconcileResult.Success success)
            {
                // A non-Success result would render a misleading report, so write no artifact for it.
                anyFailed = true;
                var detail = result switch
                {
                    ReconcileResult.Degraded d => $"Degraded — {d.Reason}",
                    ReconcileResult.Failure f => $"Failure — {f.Reason}",
                    ReconcileResult.NotNews n => $"NotNews — {n.ReasoningTrace}",
                    _ => result.GetType().Name,
                };
                Log($"  ✗ {scenario.Name}: reconcile did not succeed ({detail}); no artifact written.");
                continue;
            }
            Log($"  ✓ {scenario.Name} reconciled — {success.Tokens} tokens.");

            var htmlByLang = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var lang in renderLangs)
            {
                // Natural units per language (en→imperial, target→metric) so the judge isn't distracted by
                // Fahrenheit in a German report; the renderer formats quantity tokens per recipient.
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
                await File.WriteAllTextAsync(Path.Combine(packageDir, $"{lang}.{stamp}.{scenario.Name}.html"), html, ct);
            }

            rendered.Add(new RenderedScenario(
                scenario.Name,
                scenario.Synopsis,
                htmlByLang.GetValueOrDefault("en", ""),
                htmlByLang.GetValueOrDefault(targetIso, htmlByLang.GetValueOrDefault("en", ""))));
        }

        // en is the reference language (nothing to audit), and with no rendered scenario there's no request.
        if (targetIso == "en" || rendered.Count == 0)
            return new Result(targetIso, stamp, packageDir, rendered.Count, anyFailed, null, null, false);

        // WX-217: assemble the judging request from the rendered reports + the paired vocabulary (a DB read).
        List<VocabularyPair> vocabulary;
        string? targetDisplayName;
        await using (var ctx = dbFactory())
        {
            var enRows = await ctx.LanguageTemplates.Include(t => t.Language)
                .Where(t => t.Language!.IsoCode == "en").AsNoTracking().ToListAsync(ct);
            var tgtRows = await ctx.LanguageTemplates.Include(t => t.Language)
                .Where(t => t.Language!.IsoCode == targetIso).AsNoTracking().ToListAsync(ct);
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
        var requestObj = new JudgingRequest(targetIso, targetDisplayName, rendered, vocabulary);
        // Persist the structured request so later phases never re-reconcile (Claude is non-deterministic —
        // the artifact must show exactly what the judge saw). WX-219 consumes this.
        await File.WriteAllTextAsync(requestPath, requestMarkdown, ct);
        await File.WriteAllTextAsync(Path.Combine(packageDir, $"{targetIso}.{stamp}.request.json"),
            JsonSerializer.Serialize(requestObj, TranslationQaJson.Write), ct);
        Log($"  → judging request {requestPath}");

        var judged = false;
        if (judge is not null)
        {
            Log($"Judging (en + {targetIso}) via {judge.SourceLabel} …");
            var verdict = (await judge.JudgeAsync(requestMarkdown, ct)) with { JudgedBy = judge.SourceLabel };
            await File.WriteAllTextAsync(Path.Combine(packageDir, $"{targetIso}.{stamp}.judged.json"),
                JsonSerializer.Serialize(verdict, TranslationQaJson.Write), ct);
            judged = true;
            Log("  ✓ judged.");
        }

        return new Result(targetIso, stamp, packageDir, rendered.Count, anyFailed, requestMarkdown, requestPath, judged);
    }

    // Unit prefs are language-neutral; give each render natural units so the judge isn't distracted by
    // Fahrenheit in a non-English report. The same StructuredReport renders in either unit.
    private static Recipient RecipientFor(string lang) => lang == "en" ? Imperial() : Metric();

    private static Recipient Imperial() => new()
    {
        RecipientId = "qa-en",
        Name = "QA",
        Email = "qa@example.com",
        TempUnit = "F",
        PressureUnit = "inHg",
        WindSpeedUnit = "mph",
        PrecipUnit = "in",
    };

    private static Recipient Metric() => new()
    {
        RecipientId = "qa-target",
        Name = "QA",
        Email = "qa@example.com",
        TempUnit = "C",
        PressureUnit = "kPa",
        WindSpeedUnit = "kph",
        PrecipUnit = "mm",
    };
}