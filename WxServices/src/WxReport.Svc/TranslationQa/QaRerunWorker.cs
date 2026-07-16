using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxServices.Common;
using WxServices.Logging;

namespace WxReport.Svc.TranslationQa;

/// <summary>
/// WX-235 — services operator-requested "Rerun QA" regenerations independently of the report cycle.
/// WxManager writes a <see cref="QaRerunStatus.Running"/> row (one per language) the instant the button is
/// pressed; this worker polls every <see cref="PollInterval"/>, atomically claims the oldest unclaimed row
/// (<c>Status = Running AND StartedAtUtc IS NULL → set StartedAtUtc</c>), runs the
/// <see cref="TranslationQaRunner"/> against the current DB vocabulary, and records
/// <see cref="QaRerunStatus.Succeeded"/> + the package stamp or <see cref="QaRerunStatus.Failed"/> + the
/// reason. A startup-and-poll sweep recovers a run left <see cref="QaRerunStatus.Running"/> past
/// <see cref="StuckRunTimeout"/> (e.g. a service crash mid-run) to <see cref="QaRerunStatus.Failed"/>, so a
/// button never animates forever. Both API keys are read from <see cref="GlobalSettings"/> (Id = 1).
/// </summary>
public sealed class QaRerunWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    // Generous margin over a real run (a worst-case 2-scenario reconcile with a slow Claude client can
    // approach ~10 min); only a genuinely orphaned run should ever exceed this.
    private static readonly TimeSpan StuckRunTimeout = TimeSpan.FromMinutes(30);

    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient _claudeHttpClient;
    private readonly PersonaPrefix _persona;
    private readonly LanguageTemplateStore _templates;

    /// <summary>Initializes a new <see cref="QaRerunWorker"/>.</summary>
    /// <param name="config">Application configuration (InstallRoot, Claude/Gemini/Report sections).</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/>.</param>
    /// <param name="httpClientFactory">Factory for the named <c>Claude</c> client (long reconciliation timeout, WX-100).</param>
    /// <param name="persona">Author-persona prefix threaded into every Claude call.</param>
    /// <param name="templates">DB-backed localized-template cache the renderer reads.</param>
    public QaRerunWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions,
        IHttpClientFactory httpClientFactory,
        PersonaPrefix persona,
        LanguageTemplateStore templates)
    {
        _config = config;
        _dbOptions = dbOptions;
        _claudeHttpClient = httpClientFactory.CreateClient("Claude");
        _persona = persona;
        _templates = templates;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("QaRerunWorker started.");
        try
        {
            // The first iteration sweeps before it polls, so startup recovery happens INSIDE the resilient
            // try/catch below — a transient DB error at startup then can't escape ExecuteAsync and (default
            // BackgroundServiceExceptionBehavior.StopHost) take the whole service down.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepStuckRunsAsync(stoppingToken);   // recover runs orphaned by a prior crash
                    await PollOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A poll fault must never kill the worker — log and try again next tick.
                    Logger.Error("QaRerunWorker poll failed.", ex);
                }
                // Beat after each poll (success OR handled fault) so the heartbeat tracks "this loop is
                // still turning", not "a rerun was claimed" — reruns are operator-triggered and rare.
                Heartbeat.Write(new WxPaths(_config["InstallRoot"]).HeartbeatFile(WxWorkers.ReportQa));
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        Logger.Info("QaRerunWorker stopped.");
    }

    /// <summary>Recover any run left Running past the timeout (claimed but never completed — a crash mid-run).</summary>
    private async Task SweepStuckRunsAsync(CancellationToken ct)
    {
        await using var ctx = new WeatherDataContext(_dbOptions);
        var n = await QaRerunStore.SweepStuckAsync(
            ctx, DateTime.UtcNow - StuckRunTimeout, DateTime.UtcNow,
            "interrupted — the service restarted or the run exceeded the timeout", ct);
        if (n > 0)
            Logger.Warn($"QaRerunWorker swept {n} stuck QA-rerun row(s) to Failed.");
    }

    /// <summary>Claim and execute the oldest unclaimed Running row, if any.</summary>
    private async Task PollOnceAsync(CancellationToken ct)
    {
        QaRerunStore.ClaimedRun? claim;
        await using (var ctx = new WeatherDataContext(_dbOptions))
            claim = await QaRerunStore.TryClaimNextAsync(ctx, DateTime.UtcNow, ct);
        if (claim is null)
            return;

        Logger.Info($"QaRerunWorker claimed QA rerun for '{claim.IsoCode}' (id {claim.Id}); generating …");
        try
        {
            var result = await GenerateAsync(claim.IsoCode, ct);
            var (status, stamp, error) = OutcomeFor(result);
            // Generation is done (the package is either produced or definitively not) — record the terminal
            // state durably with None, like the failure path below. If shutdown raced the recording with ct,
            // the claim would be released and the just-produced package re-run on restart (a duplicate).
            await CompleteAsync(claim.Id, claim.ClaimedAtUtc, status, resultStamp: stamp, error: error, CancellationToken.None);
            if (status == QaRerunStatus.Succeeded)
                Logger.Info($"QaRerunWorker completed QA rerun for '{claim.IsoCode}' → {stamp}.");
            else
                Logger.Warn($"QaRerunWorker QA rerun for '{claim.IsoCode}' produced no judged package; marked Failed.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown mid-run: release the claim so the next start re-runs the request, rather than stranding
            // it Running until the 30-min sweep (which would leave the operator's button "running" meanwhile).
            await ReleaseClaimAsync(claim.Id, claim.ClaimedAtUtc);
            throw;
        }
        catch (Exception ex)
        {
            await CompleteAsync(claim.Id, claim.ClaimedAtUtc, QaRerunStatus.Failed, resultStamp: null, error: Trim(ex.Message), CancellationToken.None);
            Logger.Error($"QaRerunWorker QA rerun for '{claim.IsoCode}' failed.", ex);
        }
    }

    /// <summary>Build the pipeline dependencies (keys from <see cref="GlobalSettings"/>) and run the generation.</summary>
    private async Task<TranslationQaRunner.Result> GenerateAsync(string iso, CancellationToken ct)
    {
        var installRoot = _config["InstallRoot"];
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new InvalidOperationException("InstallRoot is not configured; cannot resolve the translation-qa output folder.");
        var outDir = Path.Combine(installRoot, "translation-qa");

        var claudeCfg = _config.GetSection("Claude").Get<ClaudeConfig>() ?? new ClaudeConfig();
        var reportCfg = new ReportConfig();
        _config.GetSection("Report").Bind(reportCfg);

        var geminiCfg = new GeminiConfig();
        _config.GetSection("Gemini").Bind(geminiCfg);   // optional model/timeout/base-url overrides

        // Both secrets live in the DB (WX-235 moved the Gemini key into GlobalSettings alongside Claude).
        await using (var ctx = new WeatherDataContext(_dbOptions))
        {
            var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
            claudeCfg.ApiKey = gs?.ClaudeApiKey ?? "";
            geminiCfg.ApiKey = gs?.GeminiApiKey;   // DB is the source of truth, overriding any config value
        }
        if (string.IsNullOrWhiteSpace(claudeCfg.ApiKey))
            throw new InvalidOperationException("GlobalSettings.ClaudeApiKey is not set — cannot reconcile.");
        if (string.IsNullOrWhiteSpace(geminiCfg.ApiKey))
            throw new InvalidOperationException("GlobalSettings.GeminiApiKey is not set — cannot judge the regenerated package.");

        var reconciler = new ForecastReconciler(
            new ClaudeClient(_claudeHttpClient, claudeCfg.ApiKey!, claudeCfg.Model, _persona.Text), _templates);

        if (geminiCfg.TimeoutSeconds <= 0)
        {
            // HttpClient.Timeout rejects zero/negative with ArgumentOutOfRangeException; a misconfigured
            // Gemini:TimeoutSeconds override must not crash the rerun at client creation.
            Logger.Warn($"Gemini:TimeoutSeconds={geminiCfg.TimeoutSeconds} is not positive; using {new GeminiConfig().TimeoutSeconds}s.");
            geminiCfg.TimeoutSeconds = new GeminiConfig().TimeoutSeconds;
        }
        using var geminiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(geminiCfg.TimeoutSeconds) };
        IJudge judge = new GeminiJudge(geminiHttp, geminiCfg);

        var result = await TranslationQaRunner.RunAsync(
            iso,
            Exemplars.All(),
            outDir,
            reconciler,
            _templates,
            reportCfg,
            () => new WeatherDataContext(_dbOptions),
            judge,
            m => Logger.Info($"[QA rerun {iso}] {m}"),
            ct);

        return result;
    }

    /// <summary>
    /// Map a completed generation to its terminal state. A run is a success ONLY if it produced a judged
    /// package (<see cref="TranslationQaRunner.Result.Judged"/> — the package's visibility marker) AND every
    /// scenario reconciled (<see cref="TranslationQaRunner.Result.AnyScenarioFailed"/> is false). A run that
    /// produced no package, or only a partial one (some scenarios failed), is a FAILED regeneration — never a
    /// ✓ over a missing/partial package that would silently supersede the prior complete one (WX-235). A
    /// shutdown-cancelled run never reaches here — it throws <see cref="OperationCanceledException"/> and its
    /// claim is released for re-run on the next start.
    /// </summary>
    internal static (QaRerunStatus Status, string? Stamp, string? Error) OutcomeFor(TranslationQaRunner.Result result)
    {
        if (result.Judged && !result.AnyScenarioFailed)
            return (QaRerunStatus.Succeeded, result.Stamp, null);

        // Distinguish no-package-at-all from a partial package so the operator's ⚠ tooltip is honest.
        var reason = !result.Judged
            ? "Regeneration produced no judged package."
            : $"Regenerated package is incomplete — {result.ScenariosRendered} scenario(s) rendered but one or more failed to reconcile.";
        return (QaRerunStatus.Failed, null, reason);
    }

    /// <summary>Record the terminal state on the claimed row (guarded against a swept/re-queued row by the claim stamp).</summary>
    private async Task CompleteAsync(long id, DateTime claimedAtUtc, QaRerunStatus status, string? resultStamp, string? error, CancellationToken ct)
    {
        await using var ctx = new WeatherDataContext(_dbOptions);
        await QaRerunStore.CompleteAsync(ctx, id, claimedAtUtc, status, resultStamp, error, DateTime.UtcNow, ct);
    }

    /// <summary>Release a claim on shutdown (StartedAtUtc → null) so the request re-runs on the next start. Best-effort: a release failure during shutdown is logged, not thrown.</summary>
    private async Task ReleaseClaimAsync(long id, DateTime claimedAtUtc)
    {
        try
        {
            // Bound the best-effort release so a slow/unreachable DB can't hang the host's shutdown window.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var ctx = new WeatherDataContext(_dbOptions);
            await QaRerunStore.ReleaseClaimAsync(ctx, id, claimedAtUtc, cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error("QaRerunWorker failed to release a claim during shutdown.", ex);
        }
    }

    private static string Trim(string s) => s.Length <= 1000 ? s : s[..1000];
}