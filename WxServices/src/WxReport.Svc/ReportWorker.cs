using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxInterp;

using WxServices.Common;
using WxServices.Logging;

using Recipient = MetarParser.Data.Entities.Recipient;

namespace WxReport.Svc;

/// <summary>
/// Background service that periodically evaluates each configured recipient
/// and sends a weather report email when either:
/// <list type="bullet">
///   <item>The recipient's daily scheduled hour has arrived and no scheduled
///   report has yet been sent today (in the recipient's local timezone).</item>
///   <item>A significant weather change has been detected since the last send
///   and the minimum gap between sends has elapsed.</item>
/// </list>
/// </summary>
public sealed class ReportWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient _claudeHttpClient;
    private readonly PersonaPrefix _persona;
    private readonly LanguageTemplateStore _templates;

    private readonly Meter _meter = new("WxReport.Svc", "1.0.0");
    private readonly Counter<long> _reportCycles;
    private readonly Counter<long> _reportsSent;
    private readonly Counter<long> _sendFailures;
    private readonly Counter<long> _statePersistFailures;
    private readonly Counter<long> _claudeCalls;
    private readonly Counter<long> _claudeInputTokens;
    private readonly Counter<long> _claudeOutputTokens;
    private readonly Counter<long> _claudeCacheReadTokens;
    private readonly Counter<long> _claudeCacheCreationTokens;
    private readonly Counter<long> _claudeToolUseSuccess;
    private readonly Counter<long> _claudeMalformedOutput;
    private readonly Counter<long> _triggers;
    private readonly Counter<long> _preFilterSkips;
    private readonly Counter<long> _claudeNotNews;
    private readonly Counter<long> _redundantSuppressed;
    private readonly Counter<long> _severeFlipSuppressed;
    private readonly Counter<long> _significanceGateSkips;
    private readonly Counter<long> _debounceSuppressed;
    private readonly Counter<long> _quietWindowSuppressed;
    private readonly Counter<long> _degradeBreaker;
    private readonly Histogram<double> _cycleDuration;
    private readonly Histogram<double> _claudeDuration;

    // WX-148: how near a severe block must fall for a DEGRADED (narrative-less) cycle
    // to still send a hazard report. Deliberately its own constant, distinct from
    // SignificanceGate's tier horizons — this is "is there a hazard worth interrupting
    // for, right now", not a significance-tier edge.
    private static readonly TimeSpan DegradeHazardHorizon = TimeSpan.FromHours(36);

    /// <summary>Initializes a new instance of <see cref="ReportWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to load the <c>Report</c> config section each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> to read/write recipient state.</param>
    /// <param name="httpClientFactory">Factory for the named <c>Claude</c> client (reconciliation, long timeout per WX-100).  Address geocoding / airport lookup now happens at locality-setup time in WxManager (WX-126/127), so the runtime worker no longer needs the <c>WxReport</c> client.</param>
    /// <param name="persona">Author-persona prefix loaded once at startup and threaded into every Claude call.</param>
    /// <param name="templates">The eager DB-backed localized-template cache (WX-171) the renderer reads atomic phrases from; also drives the per-recipient fail-closed send gate.</param>
    public ReportWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions,
        IHttpClientFactory httpClientFactory,
        PersonaPrefix persona,
        LanguageTemplateStore templates)
    {
        _config = config;
        _dbOptions = dbOptions;
        // The Claude client carries a long reconciliation timeout (WX-100); it is
        // the only outbound HTTP the per-locality worker makes (WX-130).
        _claudeHttpClient = httpClientFactory.CreateClient("Claude");
        _persona = persona;
        _templates = templates;
        _reportCycles = _meter.CreateCounter<long>("wxreport.cycles.total", description: "Number of completed report cycles.");
        _reportsSent = _meter.CreateCounter<long>("wxreport.sends.total", description: "Number of reports successfully sent.");
        _sendFailures = _meter.CreateCounter<long>("wxreport.send.failures.total", description: "Number of failed email sends.");
        _statePersistFailures = _meter.CreateCounter<long>("wxreport.send.state_persist_failures.total", description: "Post-send DB writes (the SentAtUtc stamp or the locality-baseline advance) that failed AFTER an email was already delivered — the send can't be un-sent, but the fact wasn't fully recorded, so the baseline may not advance and next cycle could resend. A first-class signal to alarm on, not a silent log line.");
        _claudeCalls = _meter.CreateCounter<long>("wxreport.claude.calls.total", description: "Number of Claude API calls.");
        _claudeInputTokens = _meter.CreateCounter<long>("wxreport.claude.tokens.input.total", description: "Total billed input tokens (cached + uncached) across reconciliation + template-generation calls.");
        _claudeOutputTokens = _meter.CreateCounter<long>("wxreport.claude.tokens.output.total", description: "Total output tokens generated across reconciliation + template-generation calls.");
        _claudeCacheReadTokens = _meter.CreateCounter<long>("wxreport.claude.cache.read.total", description: "Total input tokens served from prior cache writes (cache hits).");
        _claudeCacheCreationTokens = _meter.CreateCounter<long>("wxreport.claude.cache.write.total", description: "Total input tokens written to the cache (cache misses with cacheable prefix).");
        _claudeToolUseSuccess = _meter.CreateCounter<long>("wxreport.claude.tool_use.success.total", description: "Number of reconciliation calls returning a parseable three-artifact tool_use response.");
        _claudeMalformedOutput = _meter.CreateCounter<long>("wxreport.claude.malformed_output.total", description: "Number of reconciliation calls failing schema validation (skip-and-log path).");
        _triggers = _meter.CreateCounter<long>("wxreport.triggers.total", description: "Send triggers fired, tagged by trigger.type (first, scheduled, metar, taf, gfs, multiple).");
        _preFilterSkips = _meter.CreateCounter<long>("wxreport.prefilter.skips.total", description: "Cycles where no input advanced since the last Claude call, so the call was skipped (pre-filter no-op).");
        _claudeNotNews = _meter.CreateCounter<long>("wxreport.claude.not_news.total", description: "Reconciliation calls where Claude's invalidation gate judged the evidence not news and suppressed the send.");
        _redundantSuppressed = _meter.CreateCounter<long>("wxreport.suppressed.redundant.total", description: "WX-108: unscheduled sends suppressed because the reconciled snapshot was materially identical to the last sent report.");
        _severeFlipSuppressed = _meter.CreateCounter<long>("wxreport.suppressed.severe_flip.total", description: "WX-108: unscheduled sends suppressed because the only change was a severe-flag flip on an observation-only advance with no newer GFS run or TAF.");
        _significanceGateSkips = _meter.CreateCounter<long>("wxreport.suppressed.significance_gate.total", description: "WX-114: cycles the deterministic significance gate found unchanged since the last sent report, tagged by mode (enforce = Claude call skipped; shadow = would-skip but Claude still called).");
        _debounceSuppressed = _meter.CreateCounter<long>("wxreport.suppressed.debounce.total", description: "WX-181: significant unscheduled cycles suppressed by the day-banded debounce (the change's day-band min-gap had not elapsed since the last unscheduled send), tagged by mode (enforce = Claude call skipped; shadow = would-skip but Claude still called).");
        _quietWindowSuppressed = _meter.CreateCounter<long>("wxreport.suppressed.quietwindow.total", description: "WX-157: significant unscheduled cycles suppressed by the day-banded pre-scheduled quiet window (the next scheduled slot fell within the change's day-band quiet window; content rides the scheduled report), tagged by mode (enforce = Claude call skipped; shadow = would-skip but Claude still called).");
        _degradeBreaker = _meter.CreateCounter<long>("wxreport.degrade_breaker.total", description: "WX-182: due scheduled/first cycles where the degrade circuit-breaker skipped the Claude reconcile because the input is identical to the last degrade-to-no-send, tagged by outcome (cached_send = scheduled report re-issued from the last good snapshot, no Claude call; skip = no usable cached snapshot, slot deferred to self-heal on the next input change).");
        _cycleDuration = _meter.CreateHistogram<double>("wxreport.cycle.duration.seconds", unit: "s", description: "Duration of each report cycle.");
        _claudeDuration = _meter.CreateHistogram<double>("wxreport.claude.duration.seconds", unit: "s", description: "Duration of each Claude API call.");
    }

    /// <summary>
    /// Entry point called by the .NET hosted-service infrastructure.
    /// Sends an immediate out-of-cycle startup report to the first configured
    /// recipient, then runs <see cref="RunCycleAsync"/> in a loop, sleeping for
    /// <c>Report:IntervalMinutes</c> between iterations, until the host requests shutdown.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    /// <sideeffects>
    /// Writes log entries on start, each cycle, and on stop.
    /// <see cref="Microsoft.Data.SqlClient.SqlException"/> is caught and logged as a warning
    /// rather than an error for both the startup report and each normal cycle, so transient
    /// database unavailability does not surface as an unhandled exception.
    /// </sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("ReportWorker started.");

        try
        {
            await SendStartupReportAsync(stoppingToken);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (!stoppingToken.IsCancellationRequested)
        {
            Logger.Warn($"Database unavailable during startup report — will continue to normal cycle. ({ex.Message})");
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            Logger.Error("Unhandled exception in startup report.", ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.Warn($"Database unavailable — will retry next cycle. ({ex.Message})");
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.Error("Unhandled exception in report cycle.", ex);
            }

            var intervalMinutes = _config.GetValue<int>("Report:IntervalMinutes", 5);
            if (intervalMinutes <= 0)
            {
                Logger.Warn($"Report:IntervalMinutes is {intervalMinutes} — must be > 0. Using 1 minute.");
                intervalMinutes = 1;
            }
            Logger.Info($"Next report check in {intervalMinutes} minute(s).");
            try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { }
        }

        Logger.Info("ReportWorker stopped.");
    }

    // ── startup report ────────────────────────────────────────────────────────

    /// <summary>
    /// Sends an immediate out-of-cycle DIAGNOSTIC report to the FIRST valid
    /// recipient only (the operator — Paul), not to the locality's other members.
    /// Called once at service startup so a deployment can be verified without
    /// waiting for the scheduled send hour.  All send-decision logic is bypassed;
    /// the recipient's locality is reconciled unconditionally (<c>allowSkip: false</c>)
    /// as long as weather data is available, then rendered for that one recipient.
    /// The resulting <see cref="CommittedSend"/> row is flagged
    /// <see cref="CommittedSend.IsDiagnostic"/>, so it is excluded from the
    /// prior-snapshot baseline and the locality's last-sent state is NOT advanced —
    /// a deploy-time test report can't shift the shared baseline the next real
    /// cycle reconciles against, nor seed a subsequent unscheduled update
    /// (WX-130/WX-133).
    /// </summary>
    /// <param name="ct">Cancellation token propagated to database and HTTP operations.</param>
    /// <sideeffects>
    /// Sends one email (to the first valid recipient) via SMTP.
    /// Makes one HTTP call to the Claude API.
    /// Writes <see cref="ForecastSnapshot"/> and one diagnostic <see cref="CommittedSend"/>
    /// row; does NOT touch <see cref="LocalityState"/>.  Writes log entries for each step.
    /// </sideeffects>
    private async Task SendStartupReportAsync(CancellationToken ct)
    {
        await using var ctx = new WeatherDataContext(_dbOptions);
        var (cfg, smtp, claude_cfg) = await LoadConfigsAsync(ctx, ct);

        if (string.IsNullOrWhiteSpace(claude_cfg.ApiKey))
        {
            Logger.Warn("Claude.ApiKey is not set — skipping startup report.");
            return;
        }

        // The startup report is a deploy verification that goes ONLY to the first
        // valid, locality-assigned recipient (the operator), never fanned out to a
        // locality's other members. It is reconciled through the real per-locality
        // path but delivered to just that one recipient, and flagged IsDiagnostic so
        // its snapshot is excluded from the baseline and can't seed a later update.
        var recipients = await ctx.Recipients.OrderBy(r => r.Id).ToListAsync(ct);
        var duplicateIds = recipients
            .GroupBy(r => r.RecipientId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var startup = recipients.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.Email)
            && !string.IsNullOrWhiteSpace(r.RecipientId)
            && !duplicateIds.Contains(r.RecipientId)
            && r.LocalityId is not null);
        if (startup is null)
        {
            Logger.Debug("No valid locality-assigned recipient found for startup report.");
            return;
        }

        var locality = await ctx.Localities.FirstOrDefaultAsync(l => l.Id == startup.LocalityId!.Value, ct);
        if (locality is null)
        {
            Logger.Warn($"Startup report: recipient {startup.RecipientId}'s locality Id={startup.LocalityId} has no Localities row — skipping.");
            return;
        }
        var members = new List<Recipient> { startup };
        var label = $"locality '{locality.Name}' (Id={locality.Id})";

        var tz = ResolveTimezone(locality.Timezone);
        var preferredIcaos = ParseIcaoList(locality.MetarIcao);
        var snapshot = await WxInterpreter.GetSnapshotAsync(
            preferredIcaos, locality.TafIcao == "NONE" ? null : locality.TafIcao, locality.Name, _dbOptions,
            homeLat: locality.CentroidLat, homeLon: locality.CentroidLon,
            precipThresholdMmHr: cfg.PrecipRateThresholdMmHr,
            ct: ct);

        if (snapshot is null)
        {
            Logger.Warn($"{label}: no METAR, TAF, or GFS data for startup report — skipping.");
            return;
        }

        Logger.Info($"{label}: sending startup (diagnostic) report to {startup.RecipientId} {startup.Email} ({startup.Name}) only.");

        // WX-171: resolve the renderable (complete, canonicalized) language first; if the startup
        // recipient's language is incomplete, skip the diagnostic BEFORE creating the audit anchor
        // or calling Claude, so a broken-templates startup doesn't orphan a snapshot (CodeRabbit).
        // Canonicalize the code so the requested narrative set matches the renderer's normalized lookup.
        var langById = await ctx.Languages.ToDictionaryAsync(l => l.Id, ct);
        var narrativeLanguages = members
            .Select(m => LanguageTemplateStore.CanonicalIso(ResolveLanguageCode(m, langById, cfg.DefaultLanguage)))
            .Distinct(StringComparer.Ordinal)
            .Where(iso => _templates.MissingTokens(iso, Tok.All).Count == 0)
            .ToList();
        if (narrativeLanguages.Count == 0)
        {
            Logger.Error($"{label}: startup recipient's language template set is incomplete — skipping the diagnostic " +
                "(no audit anchor, no Claude call). (WX-171 fail-closed pre-filter.)");
            return;
        }

        // Pre-Claude audit anchor (matches the cycle path); the diagnostic prior
        // lookup deliberately excludes diagnostic rows so a prior real send is the
        // baseline, never an earlier startup test.
        var snapshotKey = !string.IsNullOrWhiteSpace(snapshot.StationIcao)
            ? snapshot.StationIcao
            : (snapshot.TafStationIcao ?? "");
        var anchorSnapshot = await BuildProvisionalForecastSnapshotAsync(
            snapshotKey, locality.CentroidLat, locality.CentroidLon, tz, DateTime.UtcNow, ct);
        ctx.ForecastSnapshots.Add(anchorSnapshot);
        await ctx.SaveChangesAsync(ct);

        var memberIds = members.Select(m => m.RecipientId).ToList();
        var priorSnapshot = await ctx.CommittedSends
            .Where(cs => memberIds.Contains(cs.RecipientId) && cs.SentAtUtc.HasValue && !cs.IsDiagnostic)
            .Select(cs => cs.ForecastSnapshot)
            .Where(s => s.Id != anchorSnapshot.Id)
            .OrderByDescending(s => s.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);

        var provisionalBody = ForecastSnapshotBody.Deserialize(anchorSnapshot.Body);

        var reconciler = new ForecastReconciler(
            new ClaudeClient(_claudeHttpClient, claude_cfg.ApiKey, claude_cfg.Model, _persona.Text), _templates);

        var reconcileResult = await reconciler.ReconcileAsync(
            snapshot, provisionalBody,
            snapshot.GfsForecast?.ModelRunUtc,
            snapshot.TafIssuanceUtc, snapshot.TafValidToUtc,
            priorSnapshot,
            narrativeLanguages, tz,
            reportKind: ReportKind.Diagnostic,
            previousMetarIcao: null,
            allowSkip: false, // startup is an unconditional verification send — never skippable
            changedSinceLastSend: Array.Empty<TriggerSource>(), // unused on a guaranteed send
            significanceCfg: cfg.SignificanceGate,
            nowUtc: DateTime.UtcNow,
            ct: ct);

        // allowSkip:false, so ReconcileAsync never returns NotNews — a stray
        // skip_send is converted to Failure at the reconciler and handled here. A
        // WX-148 Degraded (clean snapshot, unusable narrative) is treated like a
        // failure for the startup *diagnostic* send: a deploy verification doesn't
        // degrade-and-send a hazard report (it never advances the baseline anyway).
        if (reconcileResult is not ReconcileResult.Success success)
        {
            _claudeMalformedOutput.Add(1);
            var reason = reconcileResult switch
            {
                ReconcileResult.Failure f => f.Reason,
                ReconcileResult.Degraded d => $"degraded ({d.Reason})",
                _ => reconcileResult.GetType().Name,
            };
            Logger.Error($"{label}: reconciliation failed for startup send: {reason}. Provisional ForecastSnapshot Id={anchorSnapshot.Id} left as audit.");
            return;
        }

        _claudeToolUseSuccess.Add(1);
        AddClaudeTokens(success.Tokens);
        LogClaudeTokens(label, success.Tokens, "startup");

        var reconciledSnapshot = new ForecastSnapshot
        {
            StationIcao = snapshot.StationIcao,
            GeneratedAtUtc = DateTime.UtcNow,
            SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
            Body = success.FinalSnapshot.Serialize(),
        };
        ctx.ForecastSnapshots.Add(reconciledSnapshot);
        await ctx.SaveChangesAsync(ct);

        var nowUtc = DateTime.UtcNow;   // one send instant for the whole fan-out (WX-188 grid trim)

        // WX-193: a diagnostic report follows the same band rule as a scheduled one — it carries the
        // "What's changed" band only for a near-term severe onset. WX-189 made the change set
        // deterministic, so the band renders from the computed changes[] via the fallback even when
        // Claude wrote no changeSummary; without this strip a diagnostic surfaced a band for ordinary
        // non-severe changes. Decided once here — the report is shared across members.
        var outboundReport = StripChangeBandUnlessSevereOnset(
            success.StructuredReport, success.FinalSnapshot, priorSnapshot, ReportKind.Diagnostic, nowUtc, tz, label);

        var structuredReportJson = outboundReport.Serialize();
        var emailer = new SmtpSender(smtp, "WxReport");
        var plotsDir = new WxPaths(_config["InstallRoot"]).PlotsDir;

        // Render + send a diagnostic email for each member. No LocalityState update
        // (IsDiagnostic rows never advance the baseline). Welcome is suppressed —
        // a deploy verification is not a recipient's genuine first report.
        foreach (var member in members)
        {
            try
            {
                var langCode = ResolveLanguageCode(member, langById, cfg.DefaultLanguage);
                if (!TryResolveLanguage(langCode, member, label, out var templates, out var culture))
                    continue;  // incomplete language: logged ERROR, this recipient fails closed; others proceed
                var innerBody = StructuredReportRenderer.Render(
                    outboundReport, success.FinalSnapshot, snapshot, member, templates, culture, tz, ReportKind.Diagnostic, nowUtc);
                var report = WrapAsEmailHtml(innerBody, langCode, snapshot, tz);

                var committedSend = new CommittedSend
                {
                    ForecastSnapshot = reconciledSnapshot,
                    RecipientId = member.RecipientId,
                    ReasoningTrace = success.ReasoningTrace,
                    StructuredReport = structuredReportJson,
                    EmailBody = report,
                    CreatedAtUtc = DateTime.UtcNow,
                    IsDiagnostic = true,
                };
                ctx.CommittedSends.Add(committedSend);
                await ctx.SaveChangesAsync(ct);

                var subject = BuildSubject(snapshot, templates, culture, tz, ReportKind.Diagnostic, recipientName: member.Name, severeBody: success.FinalSnapshot);
                var plainFallback = SnapshotDescriber.Describe(snapshot, tz, ToUnitPreferences(member));

                var meteogramPath = FindMeteogramAbbrevPath(
                    preferredIcaos.Count > 0 ? preferredIcaos[0] : "", member.TempUnit, locality.Timezone, plotsDir);
                report = meteogramPath is not null
                    ? InsertMeteogramImage(report)
                    : report.Replace("<!--meteogram-->", "", StringComparison.Ordinal);
                IReadOnlyDictionary<string, string>? inlineImages = meteogramPath is not null
                    ? new Dictionary<string, string> { ["meteogramAbbrev"] = meteogramPath }
                    : null;

                var sent = await emailer.SendAsync(
                    member.Email, subject, plainFallback,
                    htmlBody: report, inlineImages: inlineImages,
                    toName: member.Name, ct: ct);

                if (!sent) { _sendFailures.Add(1); continue; }

                _reportsSent.Add(1);
                committedSend.SentAtUtc = DateTime.UtcNow;
                try { await ctx.SaveChangesAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _statePersistFailures.Add(1);
                    Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to persist CommittedSend.SentAtUtc Id={committedSend.Id} after a successful startup send.", ex);
                }
                Logger.Info($"{member.RecipientId} {member.Email} ({member.Name}): startup (diagnostic) report sent ({label}).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to render or send startup report for {label}.", ex);
            }
        }
    }

    // ── cycle ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes one full report cycle: groups recipients by locality, and for each
    /// locality builds one weather snapshot, evaluates send conditions, makes at most
    /// one Claude reconciliation call, and renders + emails it per member (WX-123/WX-130).
    /// Localities or members that fail validation are skipped for this cycle only.
    /// </summary>
    /// <param name="ct">Cancellation token propagated to database and delay operations.</param>
    /// <sideeffects>
    /// Reads and writes <see cref="LocalityState"/> + <see cref="CommittedSend"/> rows in the database.
    /// Sends email via SMTP for each qualifying member.
    /// Makes one HTTP call to the Claude API per due locality.
    /// Writes log entries for each locality decision.
    /// </sideeffects>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        Logger.Info("Starting report cycle.");
        var cycleSw = Stopwatch.StartNew();
        await using var ctx = new WeatherDataContext(_dbOptions);
        var (cfg, smtp, claude_cfg) = await LoadConfigsAsync(ctx, ct);

        try
        {
            if (string.IsNullOrWhiteSpace(claude_cfg.ApiKey))
            {
                Logger.Warn("Claude.ApiKey is not set — skipping report cycle.");
                return;
            }

            // Reports FIRST: do the per-locality sends (a no-op when nothing is due). The send
            // phase is its own method so its "no recipients / no localities" early-exits skip
            // only the sends, NOT the generation below (WX-211).
            await RunReportSendsAsync(ctx, cfg, smtp, claude_cfg, ct);

            // WX-172 generation-on-enable, AFTER the sends (WX-211): a pending-language
            // translation is a multi-second Claude call, so running it last means it never delays
            // a report — it only extends the cycle's tail. Reached on every cycle (even one with no
            // recipients), in its own DbContext, capped at one Claude call per cycle.
            await GeneratePendingLanguagesAsync(claude_cfg, ct);

            // The TRUE end-of-cycle marker — logged here, after BOTH the sends and the generation
            // tail, not in the send phase (WX-211/CR): it must not claim the cycle completed while
            // generation is still running, hung, or failing. (RunReportSendsAsync logs its own
            // "Report send phase complete." for the sends.)
            Logger.Info("Report cycle complete.");
            _reportCycles.Add(1);

        } // try
        finally
        {
            _cycleDuration.Record(cycleSw.Elapsed.TotalSeconds);
            WriteHeartbeat(cfg.HeartbeatFile);
        }
    }

    /// <summary>
    /// The report-send phase of a cycle (WX-211, extracted from <see cref="RunCycleAsync"/>): the
    /// per-locality Claude reconciliation + per-recipient render/send. Returns early (sending
    /// nothing) when there are no recipients or no locality-assigned members — those early-exits
    /// skip only the sends, so the caller still runs WX-172 generation afterward.
    /// </summary>
    private async Task RunReportSendsAsync(
        WeatherDataContext ctx, ReportConfig cfg, SmtpConfig smtp, ClaudeConfig claude_cfg, CancellationToken ct)
    {
        // WX-123/WX-130: the expensive Claude reconciliation runs once per LOCALITY, then renders
        // per recipient. Membership is read straight from the Recipients table (RecipientId,
        // LocalityId, units, language); the locality owns the shared stations, timezone, send
        // hours, and GFS centroid. A recipient with no locality gets no report and a logged ERROR
        // (Paul's firm rule, WX-130) — WxMonitor surfaces it by email.
        var recipients = await ctx.Recipients.OrderBy(r => r.Id).ToListAsync(ct);
        if (recipients.Count == 0)
        {
            Logger.Debug("No recipients configured.");
            return;
        }

        var duplicateIds = recipients
            .GroupBy(r => r.RecipientId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var id in duplicateIds)
            Logger.Error($"Duplicate recipient Id '{id}' — all entries with this Id will be skipped.");

        var membersByLocality = GroupMembersByLocality(recipients, duplicateIds);
        if (membersByLocality.Count == 0)
        {
            Logger.Debug("No locality-assigned recipients to process.");
            return;
        }

        var localities = await ctx.Localities
            .Where(l => membersByLocality.Keys.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        // The Languages registry is cycle-invariant; load it once here and resolve
        // every recipient's FK from it (WX-166), rather than re-querying per locality.
        var langById = await ctx.Languages.ToDictionaryAsync(l => l.Id, ct);

        // WX-171: refresh the localized-template cache once per cycle (cheap — a single
        // ~200-row query through the store's atomic-swap seam), so a language enabled
        // mid-run (WX-172 generate-on-enable) or a phrase edited since the last cycle
        // (WX-173 review round-trip) is picked up within one cycle without a restart —
        // and the per-recipient send gate below reads the current set, not a startup
        // snapshot. Polling now; WX-179 replaces it with an event-driven trigger.
        try
        {
            _templates.Reload();
        }
        catch (Exception ex)
        {
            // A failed refresh must not abort the cycle: the existing in-memory snapshot
            // stays in force (last-known-good), so reports still render. Log and continue.
            Logger.Error("Per-cycle language-template refresh failed; continuing with the previously loaded templates.", ex);
        }

        var reconciler = new ForecastReconciler(
            // ApiKey is validated non-empty by RunCycleAsync before this method is called (WX-211).
            new ClaudeClient(_claudeHttpClient, claude_cfg.ApiKey!, claude_cfg.Model, _persona.Text), _templates);
        var emailer = new SmtpSender(smtp, "WxReport");
        var now = DateTime.UtcNow;
        var reportsSent = 0;

        foreach (var (localityId, members) in membersByLocality)
        {
            if (!localities.TryGetValue(localityId, out var locality))
            {
                Logger.Error($"Locality Id={localityId} referenced by {members.Count} recipient(s) has no Localities row — skipping. (Membership FK integrity issue; check WxManager → Localities.)");
                continue;
            }

            reportsSent += await ProcessLocalityAsync(ctx, locality, members, reconciler, emailer, cfg, langById, now, ct);
        }

        Logger.Info(reportsSent > 0
            ? $"Report send phase complete. {reportsSent} report(s) sent."
            : "Report send phase complete. No reports due.");
    }

    /// <summary>
    /// WX-172 generation-on-enable. For each enabled language not yet generated
    /// (<see cref="Language.GeneratedAtUtc"/> IS NULL — a PENDING first enable or a prior
    /// FAILED attempt), produce its localized templates from the baseline (en) set via one
    /// Claude call and persist the outcome:
    /// <list type="bullet">
    ///   <item>READY — all tokens representable: rows written, <c>GeneratedAtUtc</c> set, <c>GenerationError</c> null.</item>
    ///   <item>BLOCKED — ≥1 token not representable: rows written (the blocked ones <c>Representable=false</c>), <c>GeneratedAtUtc</c> set, <c>GenerationError</c> describing the offending tokens. Not retried (GeneratedAtUtc is set).</item>
    ///   <item>FAILED — transport/parse/exhausted validation: <c>GenerationError</c> set, <c>GeneratedAtUtc</c> left null so the next cycle retries.</item>
    /// </list>
    /// The baseline (en) itself and any already-complete language (the seeded es, or a
    /// migration-backfilled row that slipped through) is stamped READY without a Claude
    /// call. Each language is isolated — one failure logs and never aborts the cycle or the
    /// others.
    /// </summary>
    private async Task GeneratePendingLanguagesAsync(ClaudeConfig claudeCfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claudeCfg.ApiKey))
            return;   // no key — already warned at the cycle top; nothing to generate with

        // Own DbContext: generation's tracking and SaveChanges are isolated from the
        // cycle's per-locality context, so a trailing save can never sweep in a partial
        // locality write, and the entities mutated here are fresh.
        await using var ctx = new WeatherDataContext(_dbOptions);

        // Scan PENDING (no error) before FAILED (error set) so a persistently-failing language
        // can't starve a fresh enable out of the single per-cycle generation slot.
        var pending = await ctx.Languages
            .Where(l => l.IsEnabled && l.GeneratedAtUtc == null)   // PENDING or FAILED (both retry)
            .OrderBy(l => l.GenerationError != null)
            .ThenBy(l => l.IsoCode)
            .ToListAsync(ct);
        if (pending.Count == 0)
            return;

        // Refresh the template cache before the self-heal check below, so the "already complete"
        // shortcut (`_templates.MissingTokens`) reads current state regardless of whether the send
        // phase reloaded this cycle (on a no-recipient cycle it returned before its own Reload).
        // Only paid when there is pending generation work — i.e. just after a language was enabled.
        try { _templates.Reload(); }
        catch (Exception ex) { Logger.Error("WX-211: template refresh before generation failed; using the last-loaded snapshot.", ex); }

        // The baseline (en) rows are the source for every translation; load once.
        var baselineRows = await ctx.LanguageTemplates
            .Include(t => t.Language)
            .Where(t => t.Language!.IsoCode == SupportedLanguages.BaselineCode)
            .ToListAsync(ct);
        var baselineByToken = baselineRows.Where(r => r.Representable).ToDictionary(r => r.Token, StringComparer.Ordinal);
        if (baselineByToken.Count == 0)
        {
            Logger.Error("WX-172: baseline (en) templates are missing — cannot generate any language. Resolve the seed/migration.");
            return;
        }

        var translator = new TemplateTranslator(
            new ClaudeClient(_claudeHttpClient, claudeCfg.ApiKey, claudeCfg.Model, _persona.Text));

        // At most ONE Claude-backed generation per cycle: it's a slow call and bounds both
        // cycle latency (the heartbeat is written after this) and burst cost when several
        // languages are enabled at once. Free READY-stamps (baseline / already-complete) do
        // not count — they cost nothing — so the backlog of real generations drains one per
        // cycle, harmlessly, since a not-yet-READY language has no recipients.
        bool spentGeneration = false;

        foreach (var lang in pending)
        {
            var iso = LanguageTemplateStore.CanonicalIso(lang.IsoCode);
            try
            {
                // The baseline, or any language already carrying a complete representable
                // set, is ready as-is — stamp it without spending a Claude call (the
                // migration normally does this; this self-heals anything it missed). READY
                // also requires a CultureName (date/number formatting); the baseline and the
                // seeded languages always carry one. A complete-but-culture-less language is
                // NOT stamped here — it falls through to generation, which supplies the
                // culture, so READY never implies a silent en-US locale fallback (matching the
                // WX-172-verify 'ready_no_culture' invariant).
                bool complete = string.Equals(iso, SupportedLanguages.BaselineCode, StringComparison.Ordinal)
                    || _templates.MissingTokens(iso, Tok.All).Count == 0;
                if (complete && !string.IsNullOrWhiteSpace(lang.CultureName))
                {
                    lang.GeneratedAtUtc = DateTime.UtcNow;
                    lang.GenerationError = null;
                    await ctx.SaveChangesAsync(ct);
                    Logger.Info($"WX-172: '{iso}' already has a complete template set — marked READY without generation.");
                    continue;
                }

                if (spentGeneration)
                {
                    Logger.Info($"WX-172: deferring generation of '{iso}' to a later cycle (one generation per cycle).");
                    continue;
                }
                spentGeneration = true;

                Logger.Info($"WX-172: generating templates for '{iso}' ({lang.DisplayName})…");
                var result = await translator.GenerateAsync(lang.DisplayName, iso, baselineRows, ct);

                // Record the spend on the cost counters + the per-call DEBUG line — these are
                // the heaviest single Claude calls, and the scarce LLM resource must be visible
                // on the same dashboards the reconciler feeds (a failed attempt is still billed).
                // One logical call per language generated (WX-211), matching how the reconciler
                // counts a call regardless of its internal retries.
                var usage = result switch
                {
                    TranslateResult.Success su => su.Usage,
                    TranslateResult.Failure fa => fa.Usage,
                    _ => new TokenUsage(0, 0, 0, 0),
                };
                _claudeCalls.Add(1);
                AddClaudeTokens(usage);
                LogClaudeTokens($"translate:{iso}", usage, "WX-172 template generation");

                if (result is TranslateResult.Success s)
                {
                    // Replace any prior rows for this language (a partial from an earlier
                    // attempt) with the freshly generated, validated set. Delete-then-save
                    // BEFORE inserting, so a re-used (LanguageId, Token) can't collide with a
                    // not-yet-deleted row under the unique index.
                    var existing = await ctx.LanguageTemplates.Where(t => t.LanguageId == lang.Id).ToListAsync(ct);
                    if (existing.Count > 0)
                    {
                        ctx.LanguageTemplates.RemoveRange(existing);
                        await ctx.SaveChangesAsync(ct);
                    }
                    foreach (var tr in s.Translations)
                    {
                        var baseRow = baselineByToken[tr.Token];
                        ctx.LanguageTemplates.Add(new LanguageTemplate
                        {
                            LanguageId = lang.Id,
                            Token = tr.Token,
                            Phrase = tr.Phrase,
                            // A Hint context is a language-neutral English gloss that must stay
                            // English in every language; keep the baseline's verbatim rather than
                            // Claude's (which the guidance tells it to echo unchanged anyway).
                            ContextInfo = baseRow.ContextKind == TemplateContextKind.Hint
                                ? baseRow.ContextInfo
                                : tr.TranslatedContext,
                            ContextKind = baseRow.ContextKind,   // kind is the token's, not the language's
                            Note = tr.Note,
                            Representable = tr.Representable,
                            // ReviewedBy/ReviewedAtUtc left null — generated-but-unreviewed (WX-173 round-trip).
                        });
                    }

                    var blocked = s.Translations.Where(t => !t.Representable).Select(t => t.Token).ToList();
                    lang.CultureName = s.CultureName;
                    lang.GeneratedAtUtc = DateTime.UtcNow;
                    lang.GenerationError = blocked.Count == 0
                        ? null
                        : FitGenerationError($"{blocked.Count} token(s) cannot be expressed in {lang.DisplayName} by simple substitution: "
                          + $"{string.Join(", ", blocked)}. This needs a renderer/code change; it will not retry.");
                    await ctx.SaveChangesAsync(ct);

                    Logger.Info(blocked.Count == 0
                        ? $"WX-172: '{iso}' generated and READY — {s.Translations.Count} token(s)."
                        : $"WX-172: '{iso}' BLOCKED — {blocked.Count} of {s.Translations.Count} token(s) not representable: {string.Join(", ", blocked)}.");
                }
                else if (result is TranslateResult.Failure f)
                {
                    // FAILED: record the transient reason but leave GeneratedAtUtc null so
                    // the next cycle retries. No rows are written on failure.
                    lang.GenerationError = FitGenerationError($"Generation failed (will retry next cycle): {f.Reason}");
                    await ctx.SaveChangesAsync(ct);
                    Logger.Error($"WX-172: '{iso}' generation FAILED — {f.Reason}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Isolate one language's failure from the rest of the cycle. Leave it PENDING
                // (GeneratedAtUtc still null) so the next cycle retries.
                Logger.Error($"WX-172: generation for '{iso}' threw; leaving it PENDING for the next cycle.", ex);
            }
        }
    }

    // Language.GenerationError is nvarchar(1000); a large blocked-token set or a verbose
    // validation error could exceed it and make SaveChangesAsync throw, which would leave the
    // language without the state transition we were persisting. Cap it to fit.
    private const int GenerationErrorMaxLength = 1000;
    private static string FitGenerationError(string value) =>
        value.Length <= GenerationErrorMaxLength ? value : value[..(GenerationErrorMaxLength - 3)] + "...";

    /// <summary>
    /// Partitions valid, locality-assigned recipients into per-locality member
    /// lists.  Recipients with no email, no <see cref="Recipient.RecipientId"/>, a
    /// duplicate id, or no <see cref="Recipient.LocalityId"/> are dropped here: the
    /// no-locality case is the firm WX-130 rule — no report, logged ERROR — so it
    /// surfaces via WxMonitor rather than being silently throttled.
    /// </summary>
    private static Dictionary<long, List<Recipient>> GroupMembersByLocality(
        IReadOnlyList<Recipient> recipients, IReadOnlySet<string> duplicateIds)
    {
        var byLocality = new Dictionary<long, List<Recipient>>();
        foreach (var r in recipients)
        {
            if (string.IsNullOrWhiteSpace(r.Email)) continue;
            if (string.IsNullOrWhiteSpace(r.RecipientId))
            {
                Logger.Warn($"{r.Email} ({r.Name}): no RecipientId — skipping. Set a unique Id via WxManager → Recipients.");
                continue;
            }
            if (duplicateIds.Contains(r.RecipientId)) continue;
            if (r.LocalityId is not long localityId)
            {
                Logger.Error($"{r.RecipientId} {r.Email} ({r.Name}): not assigned to a locality — no report sent. Assign one via WxManager → Recipients.");
                continue;
            }
            if (!byLocality.TryGetValue(localityId, out var list))
                byLocality[localityId] = list = new List<Recipient>();
            list.Add(r);
        }
        return byLocality;
    }

    /// <summary>
    /// Runs one locality's cycle: builds a single snapshot from the locality's
    /// stations + GFS centroid, evaluates the send decision against the shared
    /// <see cref="LocalityState"/>, makes at most one Claude reconciliation call,
    /// and on success renders + sends a per-recipient email for each member with
    /// no further LLM call (WX-123 economics).  Returns the number of members
    /// actually emailed (drives the cycle's send count and the state advance).
    /// </summary>
    private async Task<int> ProcessLocalityAsync(
        WeatherDataContext ctx, Locality locality, List<Recipient> members,
        ForecastReconciler reconciler, SmtpSender emailer, ReportConfig cfg,
        IReadOnlyDictionary<long, Language> langById, DateTime now, CancellationToken ct)
    {
        var label = $"locality '{locality.Name}' (Id={locality.Id})";

        // Members already delivered to (excluding diagnostic startup rows) — ONE
        // batched lookup, reused for the LocalityState bootstrap, the welcome
        // decision, and the per-member render branch below.
        var memberIds = members.Select(m => m.RecipientId).ToList();

        // Per-locality reconciliation baseline + send cadence (replaces RecipientState).
        var state = await ctx.LocalityStates.FirstOrDefaultAsync(s => s.LocalityId == locality.Id, ct);
        if (state is null)
        {
            // Bootstrap a freshly-created state from the locality's historical
            // deliveries (CodeRabbit, WX-130): the WX-130 cutover starts with an empty
            // LocalityStates table while recipients already have prior per-recipient
            // deliveries — without this, ShouldSend would see all-null cadence and
            // treat every established locality as "first", forcing an off-cadence send
            // to everyone on the first post-deploy cycle. Seed the scheduled-cadence
            // timestamp from the most-recent real delivery (so a scheduled slot the old
            // system already served this cycle isn't re-sent) plus the last station. The
            // last-sent input hash stays null: the first cycle reconciles once against
            // the historical prior snapshot to establish it. (LastUnscheduledSentUtc is
            // intentionally left null here — see the state initializer below.)
            var lastDelivered = await ctx.CommittedSends
                .Where(cs => memberIds.Contains(cs.RecipientId) && cs.SentAtUtc.HasValue && !cs.IsDiagnostic)
                .OrderByDescending(cs => cs.SentAtUtc)
                .Select(cs => new { cs.SentAtUtc, cs.ForecastSnapshot.StationIcao })
                .FirstOrDefaultAsync(ct);

            state = new LocalityState
            {
                LocalityId = locality.Id,
                LastScheduledSentUtc = lastDelivered?.SentAtUtc,
                // WX-181: left null rather than seeded from lastDelivered — that delivery may
                // have been a SCHEDULED send, and the day-banded debounce keys specifically on
                // the last *unscheduled* send, so seeding it would over-suppress a genuine
                // update on the first post-deploy cycle. The min-gap is unaffected: it uses
                // Max(scheduled, unscheduled), which still sees LastScheduledSentUtc.
                LastUnscheduledSentUtc = null,
                LastMetarIcao = lastDelivered?.StationIcao,
            };
            ctx.LocalityStates.Add(state);
        }

        var tz = ResolveTimezone(locality.Timezone);
        var scheduledHours = ParseHourList(locality.ScheduledSendHours ?? cfg.DefaultScheduledSendHours);

        // One snapshot per locality: locality stations + centroid as the GFS point.
        var preferredIcaos = ParseIcaoList(locality.MetarIcao);
        var snapshot = await WxInterpreter.GetSnapshotAsync(
            preferredIcaos, locality.TafIcao == "NONE" ? null : locality.TafIcao, locality.Name, _dbOptions,
            homeLat: locality.CentroidLat, homeLon: locality.CentroidLon,
            precipThresholdMmHr: cfg.PrecipRateThresholdMmHr,
            ct: ct);

        if (snapshot is null)
        {
            Logger.Warn($"{label}: no METAR, TAF, or GFS data available — skipping.");
            return 0;
        }
        if (!snapshot.ObservationAvailable)
            Logger.Warn($"{label}: station(s) [{string.Join(", ", preferredIcaos)}] had no data and no station within 30 mi reported in the last 3 hours — sending forecast-only report.");
        else if (preferredIcaos.Count > 0 && !preferredIcaos.Contains(snapshot.StationIcao))
        {
            var distStr = snapshot.ObservationDistanceKm is double km ? $" ({km * 0.621371:F0} mi away)" : "";
            Logger.Warn($"{label}: station(s) [{string.Join(", ", preferredIcaos)}] had no data — fell back to {snapshot.StationIcao}{distStr}.");
        }

        if (snapshot.GfsForecast is { } gfs)
            Logger.Info($"{label}: GFS run {gfs.ModelRunUtc:yyyy-MM-dd HH}Z — {gfs.Days.Count} day(s); " +
                string.Join(", ", gfs.Days.Select(d => $"{d.Date:MM/dd} {d.HighTempC:F0}°/{d.LowTempC:F0}°C")));
        else
            Logger.Warn($"{label}: no GFS forecast available.");

        // WX-80 pre-filter identity + WX-108 changed-since-last-DELIVERED context,
        // both now keyed on the locality's shared LocalityState.
        var inputIdentity = InputIdentity.From(snapshot);
        var inputHash = inputIdentity.Serialize();
        var changedSinceLastSend = inputIdentity.ChangedSourcesSince(state.LastSentInputHash);
        bool freshTafSinceLastSend = changedSinceLastSend.Contains(TriggerSource.Taf);
        bool freshGuidanceSinceLastSend = freshTafSinceLastSend || changedSinceLastSend.Contains(TriggerSource.Gfs);

        // A member NOT in this set has never been contacted and gets a welcome-only
        // first email (WX-130).
        var servedIds = (await ctx.CommittedSends
            .Where(cs => memberIds.Contains(cs.RecipientId) && cs.SentAtUtc.HasValue && !cs.IsDiagnostic)
            .Select(cs => cs.RecipientId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var (shouldSend, reason, kind, allowSkip) = ShouldSend(tz, scheduledHours, state, inputIdentity, cfg, now);
        if (!shouldSend)
        {
            // Even on a non-sending cycle, onboard any never-contacted member with a
            // standalone welcome-only email — no Claude call, no disturbance to other
            // members, anchored to the locality's most-recent delivered snapshot
            // (a !shouldSend locality has sent before, so that baseline exists).
            var unwelcomed = members.Where(m => !servedIds.Contains(m.RecipientId)).ToList();
            var welcomed = unwelcomed.Count > 0
                ? await WelcomeNewMembersAsync(ctx, emailer, locality, unwelcomed, memberIds, tz, scheduledHours, cfg, langById, ct)
                : 0;
            if (reason == "prefilter-skip" && welcomed == 0)
            {
                _preFilterSkips.Add(1);
                Logger.Debug($"{label}: no input changed since last Claude call — pre-filter skip.");
            }
            return welcomed;
        }

        // WX-182 degrade circuit-breaker: we already degraded-to-no-send on this exact
        // input, so a re-reconcile would deterministically degrade again. On the
        // guaranteed scheduled/first path (which bypasses the pre-filter), re-issue the
        // last good report from cache with no Claude call — or, if there is no usable
        // cached snapshot, skip and self-heal when the input next changes. The unscheduled
        // "change" path cannot reach here (its pre-filter already skips an identical
        // input). Strict equality keeps this orthogonal to the WX-157 quiet window: it
        // can never fire on an advanced input. A null return means a severe block has
        // crossed into the hazard horizon on this stuck input — fall through to a normal
        // reconcile so the WX-148 degrade-hazard path can send the banner (safety > cost).
        if ((reason is "scheduled" or "first") && state.LastDegradedInputHash == inputHash)
        {
            var cachedSent = await SendScheduledFromCacheAsync(
                ctx, emailer, locality, members, memberIds, servedIds, state, snapshot,
                preferredIcaos, inputHash, reason, tz, langById, cfg, now, label, ct);
            if (cachedSent is int sent)
                return sent;
        }

        var triggerType = reason == "change"
            ? ArrivalLabel(inputIdentity.ChangedSourcesSince(state.LastClaudeInputHash))
            : reason;
        _triggers.Add(1, new KeyValuePair<string, object?>("trigger.type", triggerType));
        Logger.Info(reason == "change"
            ? $"{label}: {triggerType} arrival — invoking Claude invalidation gate."
            : $"{label}: generating {reason} report.");

        // Station switch: the locality's primary station had no data and a fallback
        // is in use; pass the previous ICAO so Claude can note it in the narrative.
        var previousMetarIcao = snapshot.ObservationAvailable
            && state.LastMetarIcao is not null
            && state.LastMetarIcao != snapshot.StationIcao
                ? state.LastMetarIcao
                : null;
        if (previousMetarIcao is not null)
            Logger.Info($"{label}: METAR station changed {previousMetarIcao} → {snapshot.StationIcao} — noting in report.");

        // Pre-Claude audit anchor (minimal-schema, WX-130): persist the GFS
        // provisional projection for the locality. On a Claude failure this orphan
        // snapshot records what we were about to reconcile. Per-recipient delivery
        // rows (CommittedSend) are written only on success — there is no provisional
        // CommittedSend in the per-locality model.

        // WX-171: resolve the renderable (complete) languages up front — canonicalized to the
        // same bare iso the renderer and reconciler key on (so the requested narrative set can't
        // mismatch a normalized lookup). A recipient whose language is incomplete fails closed
        // (the per-member send gate below). If NO member has a complete language, skip the
        // locality HERE — before building/persisting the provisional snapshot or calling Claude —
        // so a broken-templates locality doesn't orphan an audit snapshot or burn a reconcile
        // every cycle (CodeRabbit). The per-member gate stays the fail-closed boundary for the mix.
        var sendableLanguages = members
            .Select(m => LanguageTemplateStore.CanonicalIso(ResolveLanguageCode(m, langById, cfg.DefaultLanguage)))
            .Distinct(StringComparer.Ordinal)
            .Where(iso => _templates.MissingTokens(iso, Tok.All).Count == 0)
            .ToList();
        if (sendableLanguages.Count == 0)
        {
            Logger.Error($"{label}: no member has a complete language template set — skipping the locality this cycle " +
                "(no provisional snapshot, no Claude reconcile, no send). Resolve by regenerating/repairing the " +
                "language templates. (WX-171 fail-closed pre-filter.)");
            return 0;
        }

        var snapshotKey = !string.IsNullOrWhiteSpace(snapshot.StationIcao)
            ? snapshot.StationIcao
            : (snapshot.TafStationIcao ?? "");
        var anchorSnapshot = await BuildProvisionalForecastSnapshotAsync(
            snapshotKey, locality.CentroidLat, locality.CentroidLon, tz, DateTime.UtcNow, ct);
        ctx.ForecastSnapshots.Add(anchorSnapshot);
        await ctx.SaveChangesAsync(ct);

        // Locality baseline: the last DELIVERED reconciled snapshot for any member,
        // excluding diagnostic sends (WX-130) and this cycle's provisional. Because
        // every member's CommittedSend references the one shared reconciled snapshot,
        // any member's most recent delivery points at the locality's baseline.
        var priorSnapshot = await ctx.CommittedSends
            .Where(cs => memberIds.Contains(cs.RecipientId) && cs.SentAtUtc.HasValue && !cs.IsDiagnostic)
            .Select(cs => cs.ForecastSnapshot)
            .Where(s => s.Id != anchorSnapshot.Id)
            .OrderByDescending(s => s.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);

        var provisionalBody = ForecastSnapshotBody.Deserialize(anchorSnapshot.Body);

        // WX-114 deterministic significance gate (cost pre-filter, unscheduled only).
        // WX-160: the gate compares a GFS+TAF MERGED body (TafBlockProjector overlays
        // the parsed TAF onto the GFS provisional), so a fresh TAF that moves nothing
        // material is now suppressed deterministically instead of bypassing the gate
        // via the retired `taf-fresh` shortcut. Wind in the merge is sustained-only
        // (gust touches a gate decision solely through the 50-kt severe rule).
        var gateMode = cfg.SignificanceGate.Mode;
        if (gateMode != SignificanceGateMode.Off && allowSkip && priorSnapshot is not null)
        {
            SignificanceResult? gate = null;
            try
            {
                var priorBodyForGate = ForecastSnapshotBody.Deserialize(priorSnapshot.Body);
                var gateCurrentBody = TafBlockProjector.Merge(provisionalBody, snapshot.ForecastPeriods, snapshot.TafValidToUtc);
                gate = SignificanceGate.Evaluate(priorBodyForGate, gateCurrentBody, cfg.SignificanceGate, now, tz);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"{label}: WX-114 significance gate evaluation failed — proceeding to Claude.", ex);
            }

            if (gate is { Significant: false })
            {
                bool enforce = gateMode == SignificanceGateMode.Enforce;
                _significanceGateSkips.Add(1, new KeyValuePair<string, object?>("mode", enforce ? "enforce" : "shadow"));
                if (enforce)
                {
                    Logger.Debug($"{label}: WX-114 significance gate suppressed {triggerType} cycle — TAF-merged forecast shows no material change since last sent report; Claude not called.");
                    await PersistUnsentCycleAsync(ctx, label, state, inputHash, ct);
                    return 0;
                }
                Logger.Debug($"{label}: WX-114 significance gate (shadow) WOULD suppress {triggerType} cycle — calling Claude anyway.");
            }
            else if (gate is { Significant: true } passed)
            {
                // Two day-banded admission-control suppressors, evaluated before the Claude
                // call: WX-181 post-send debounce, then the WX-157 pre-scheduled quiet window.
                // Each folds a significant, non-severe-onset unscheduled change away (its
                // content rides the next cycle / the scheduled report); a not-severe→severe
                // onset punches through both (inside the decision fns); the 90-min
                // MinGapMinutes floor was already enforced in ShouldSend. Both honor the gate's
                // enforce/shadow mode; the scheduled report itself is never quieted (ShouldSend
                // fires it on its slot). Debounce wins ties (checked first), matching its
                // longer window.
                if (await ApplyDayBandedSuppressorAsync(
                        ctx, state, inputHash, label, gateMode, triggerType,
                        cfg.UpdateDebounceSchedule, "UpdateDebounceSchedule", "WX-181 debounce", _debounceSuppressed,
                        sch => (ShouldDebounceUpdate(passed, state.LastUnscheduledSentUtc, sch, now, tz, out var r), r), ct))
                    return 0;

                if (await ApplyDayBandedSuppressorAsync(
                        ctx, state, inputHash, label, gateMode, triggerType,
                        cfg.PreScheduledQuietSchedule, "PreScheduledQuietSchedule", "WX-157 quiet window", _quietWindowSuppressed,
                        sch => (ShouldQuietBeforeSlot(passed, scheduledHours, sch, now, tz, out var r), r), ct))
                    return 0;

                Logger.Debug($"{label}: WX-114 significance gate passed ({gateMode}, {triggerType}) — fired: {string.Join(", ", passed.FiredCriteria)}.");
            }
        }

        // The reconcile requests narrative for exactly the renderable languages resolved above
        // (complete + canonicalized), so Claude is never asked for a language we'd discard and
        // the requested set matches the renderer's normalized narrative lookup.
        var narrativeLanguages = sendableLanguages;

        var claudeSw = Stopwatch.StartNew();
        var reconcileResult = await reconciler.ReconcileAsync(
            snapshot, provisionalBody,
            snapshot.GfsForecast?.ModelRunUtc,
            snapshot.TafIssuanceUtc, snapshot.TafValidToUtc,
            priorSnapshot,
            narrativeLanguages, tz,
            reportKind: kind,
            previousMetarIcao: previousMetarIcao,
            allowSkip: allowSkip,
            changedSinceLastSend: changedSinceLastSend,
            significanceCfg: cfg.SignificanceGate,
            nowUtc: now,
            ct: ct);
        _claudeDuration.Record(claudeSw.Elapsed.TotalSeconds);
        _claudeCalls.Add(1);

        // WX-80 invalidation gate: Claude judged the arrival not news. Advance only
        // LastClaudeInputHash (so an identical next cycle pre-filter-skips); do NOT
        // send and do NOT advance last-sent. The not-news trace is logged (the
        // minimal-schema model keeps no per-locality provisional row to carry it).
        if (reconcileResult is ReconcileResult.NotNews notNews)
        {
            _claudeNotNews.Add(1);
            AddClaudeTokens(notNews.Tokens);
            LogClaudeTokens(label, notNews.Tokens, $"{triggerType}/not-news");
            await PersistUnsentCycleAsync(ctx, label, state, inputHash, ct);
            Logger.Info($"{label}: Claude judged the {triggerType} arrival not news — no send. Trace: {notNews.ReasoningTrace}");
            return 0;
        }

        // WX-148 tier-aware degrade: Claude could not produce a self-consistent
        // narrative, but the snapshot parsed cleanly. Never withhold a hazard over a
        // prose fault — if a severe block falls in the near horizon, send a
        // narrative-less hazard report (deterministic banner + conditions + grid, no
        // summary) built from the parsed snapshot. Otherwise the missing summary isn't
        // worth an interruption: log it and self-heal next cycle.
        if (reconcileResult is ReconcileResult.Degraded degraded)
        {
            AddClaudeTokens(degraded.Tokens);
            LogClaudeTokens(label, degraded.Tokens, $"{triggerType}/degraded");
            bool hazardSoon = SevereBlocks.AnyActiveWithin(degraded.FinalSnapshot, now, DegradeHazardHorizon);
            if (!hazardSoon)
            {
                _claudeMalformedOutput.Add(1);
                // WX-182: arm the degrade circuit-breaker — record the input we just
                // degraded on so an identical next cycle skips the (deterministically
                // wasteful) re-reconcile. PersistUnsentCycleAsync saves both this and
                // LastClaudeInputHash in one write.
                state.LastDegradedInputHash = inputHash;
                await PersistUnsentCycleAsync(ctx, label, state, inputHash, ct);
                Logger.Warn($"{label}: reconciliation degraded ({degraded.Reason}); no near-term severe block, summary omitted and no send — self-heal next cycle (WX-182 breaker armed for this input).");
                return 0;
            }

            Logger.Warn($"{label}: reconciliation degraded ({degraded.Reason}); sending a narrative-less hazard report (summary omitted) to served members.");
            var degradedSnapshot = new ForecastSnapshot
            {
                StationIcao = snapshot.StationIcao,
                GeneratedAtUtc = DateTime.UtcNow,
                SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
                Body = degraded.FinalSnapshot.Serialize(),
            };
            ctx.ForecastSnapshots.Add(degradedSnapshot);
            await ctx.SaveChangesAsync(ct);

            var degradedPlotsDir = new WxPaths(_config["InstallRoot"]).PlotsDir;
            var degradedSent = 0;
            foreach (var member in members)
            {
                if (!servedIds.Contains(member.RecipientId))
                    continue;  // brand-new members are onboarded on a normal cycle, not via a degraded alert
                try
                {
                    var langCode = ResolveLanguageCode(member, langById, cfg.DefaultLanguage);
                    if (!TryResolveLanguage(langCode, member, label, out var templates, out var culture))
                        continue;  // incomplete language: this recipient fails closed; others still get the hazard report
                    var innerBody = StructuredReportRenderer.RenderDegraded(degraded.FinalSnapshot, snapshot, member, templates, culture, tz, now);
                    if (await DeliverWeatherReportAsync(
                            ctx, emailer, member, langCode, templates, culture, degradedSnapshot, innerBody,
                            structuredReportJson: null, reasoningTrace: $"DEGRADED: {degraded.Reason}",
                            snapshot, locality, tz, preferredIcaos, degradedPlotsDir, ReportKind.Unscheduled, label,
                            degraded.FinalSnapshot, ct))
                        degradedSent++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to render or send degraded report for {label}.", ex);
                }
            }

            // A degraded hazard report is still a delivered weather report: advance the
            // baseline exactly as a normal send (shared helper), so the block-valid
            // snapshot becomes the next cycle's prior and the slot is marked served.
            if (degradedSent > 0)
                await AdvanceBaselineAfterSendAsync(ctx, state, reason, now, inputHash, snapshot, label, ct);
            return degradedSent;
        }

        if (reconcileResult is not ReconcileResult.Success success)
        {
            _claudeMalformedOutput.Add(1);
            var failureReason = ((ReconcileResult.Failure)reconcileResult).Reason;
            Logger.Error($"{label}: Reconciliation failed: {failureReason}. Provisional ForecastSnapshot Id={anchorSnapshot.Id} left as audit; no send.");
            return 0;
        }

        _claudeToolUseSuccess.Add(1);
        AddClaudeTokens(success.Tokens);
        LogClaudeTokens(label, success.Tokens, $"{triggerType}/reconciled");

        // WX-108 deterministic backstop (unscheduled cycles only) — idempotence /
        // severe-flag hysteresis, not significance (Claude owns that via the gate).
        if (allowSkip && priorSnapshot is not null)
        {
            var priorBody = ForecastSnapshotBody.Deserialize(priorSnapshot.Body);
            var suppression = EvaluateUnscheduledSuppression(
                priorBody, success.FinalSnapshot, freshGuidanceSinceLastSend);
            if (suppression != UnscheduledSuppression.None)
            {
                if (suppression == UnscheduledSuppression.Redundant) _redundantSuppressed.Add(1);
                else _severeFlipSuppressed.Add(1);
                var why = suppression == UnscheduledSuppression.Redundant
                    ? "reconciled snapshot is materially identical to the last sent report (redundant re-send)"
                    : "severe-flag de-escalation on an observation-only advance with no newer GFS run or TAF (hysteresis)";
                await PersistUnsentCycleAsync(ctx, label, state, inputHash, ct);
                Logger.Info($"{label}: WX-108 suppressed {triggerType} send — {why}.");
                return 0;
            }
        }

        // Persist the reconciled snapshot ONCE; every member's CommittedSend
        // references it. The structured report + reasoning trace are copied onto
        // each per-recipient row so each delivery audit is self-contained.
        var reconciledSnapshot = new ForecastSnapshot
        {
            StationIcao = snapshot.StationIcao,
            GeneratedAtUtc = DateTime.UtcNow,
            SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
            Body = success.FinalSnapshot.Serialize(),
        };
        ctx.ForecastSnapshots.Add(reconciledSnapshot);
        await ctx.SaveChangesAsync(ct);

        // WX-178: a scheduled report shows "What's changed" only for a near-term severe onset;
        // otherwise strip the band deterministically (the reconciler prompt steers the LLM the
        // same way; this guarantees it). Decided once here — the report is shared across members.
        // The shared strip is a no-op on a non-gating (unscheduled) kind.
        var outboundReport = StripChangeBandUnlessSevereOnset(
            success.StructuredReport, success.FinalSnapshot, priorSnapshot, kind, now, tz, label);

        var structuredReportJson = outboundReport.Serialize();
        var plotsDir = new WxPaths(_config["InstallRoot"]).PlotsDir;
        var weatherSent = 0;   // weather reports delivered — drives the baseline/cadence advance
        var welcomeSent = 0;   // first-contact welcome-only emails delivered

        // Inner loop: deterministic per-recipient send — no further LLM call. A
        // never-contacted member gets a welcome-only first email (anchored to this
        // cycle's reconciled snapshot so they count as served next cycle); everyone
        // already served gets the weather report.
        foreach (var member in members)
        {
            try
            {
                var langCode = ResolveLanguageCode(member, langById, cfg.DefaultLanguage);
                if (!TryResolveLanguage(langCode, member, label, out var templates, out var culture))
                    continue;  // incomplete language: this recipient fails closed; others proceed
                if (!servedIds.Contains(member.RecipientId))
                {
                    if (await SendWelcomeAsync(ctx, emailer, member, langCode, templates, culture, locality, reconciledSnapshot, tz, scheduledHours, ct))
                        welcomeSent++;
                    continue;
                }

                var innerBody = StructuredReportRenderer.Render(
                    outboundReport, success.FinalSnapshot, snapshot, member, templates, culture, tz, kind, now);
                if (await DeliverWeatherReportAsync(
                        ctx, emailer, member, langCode, templates, culture, reconciledSnapshot, innerBody,
                        structuredReportJson, success.ReasoningTrace, snapshot, locality, tz,
                        preferredIcaos, plotsDir, kind, label, success.FinalSnapshot, ct))
                    weatherSent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to render or send report for {label}.", ex);
            }
        }

        // Advance the baseline only when a WEATHER report was actually delivered —
        // welcomes (first-contact, no weather) must NOT move it (see helper).
        if (weatherSent > 0)
            await AdvanceBaselineAfterSendAsync(ctx, state, reason, now, inputHash, snapshot, label, ct);

        return weatherSent + welcomeSent;
    }

    /// <summary>
    /// Advances the locality's baseline + cadence after a delivered WEATHER report
    /// (the normal path or the WX-148 degraded hazard report). Welcomes never call
    /// this: a first-contact email must not move the baseline or mark a scheduled slot
    /// served, else a welcome sent at/after the scheduled hour would suppress the
    /// recipient's first real scheduled report. <c>LastSentInputHash</c> advances only
    /// on a real weather delivery; not-news / suppression advance only
    /// <c>LastClaudeInputHash</c>. Shared so the normal and degraded paths cannot drift
    /// — the baseline-bleed class of bug DESIGN.md §10 documents.
    /// </summary>
    private async Task AdvanceBaselineAfterSendAsync(
        WeatherDataContext ctx, LocalityState state, string reason, DateTime now, string inputHash,
        WeatherSnapshot snapshot, string label, CancellationToken ct, bool clearDegraded = true)
    {
        if (reason is "scheduled" or "first")
            state.LastScheduledSentUtc = now;
        else
            state.LastUnscheduledSentUtc = now;
        state.LastClaudeInputHash = inputHash;
        state.LastSentInputHash = inputHash;
        // WX-182: a genuine Claude-reconciled send resets the degrade breaker; the cached
        // re-send (clearDegraded:false) leaves it armed so further scheduled slots on the
        // same stuck input stay cost-0.
        if (clearDegraded)
            state.LastDegradedInputHash = null;
        if (snapshot.ObservationAvailable)
            state.LastMetarIcao = snapshot.StationIcao;

        try { await ctx.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _statePersistFailures.Add(1);
            Logger.Error($"{label}: failed to save locality state after sends — baseline did not advance; next cycle may resend.", ex);
        }
    }

    /// <summary>
    /// WX-182 degrade circuit-breaker delivery: a due scheduled/first slot whose input is
    /// identical to the last degrade-to-no-send. Re-issues the locality's last good report
    /// from cache — the last reconciled snapshot + its narrative (change band suppressed) +
    /// the live current conditions — with NO Claude call, and returns the number sent.
    /// Returns <c>0</c> (skip; self-heal on the next input change) when there is no usable
    /// cached snapshot (e.g. a locality whose first-ever reconcile degraded). Returns
    /// <see langword="null"/> to tell the caller to fall through to a normal reconcile: a
    /// severe block fixed in the cached forecast has crossed into the hazard horizon as the
    /// clock advanced, so the WX-148 degrade-hazard path must run (safety over cost) — the
    /// breaker only ever arms on a non-hazard degrade, so a clock-advance is the sole way
    /// severe can surface here. Delivers to served members only (new members onboard on a
    /// normal reconciling cycle).
    /// </summary>
    private async Task<int?> SendScheduledFromCacheAsync(
        WeatherDataContext ctx, SmtpSender emailer, Locality locality, List<Recipient> members,
        List<string> memberIds, IReadOnlySet<string> servedIds, LocalityState state,
        WeatherSnapshot snapshot, IReadOnlyList<string> preferredIcaos, string inputHash, string reason,
        TimeZoneInfo tz, IReadOnlyDictionary<long, Language> langById, ReportConfig cfg, DateTime now,
        string label, CancellationToken ct)
    {
        int Skip(string why)
        {
            _degradeBreaker.Add(1, new KeyValuePair<string, object?>("outcome", "skip"));
            Logger.Warn($"{label}: WX-182 degrade circuit-breaker — {why}; {reason} report deferred until the input changes (self-heal).");
            return 0;
        }

        // The locality's last DELIVERED reconciled report: its snapshot + narrative. Keyed
        // on a non-null StructuredReport so a prior narrative-less degraded-hazard send is
        // not picked as the cache source.
        var lastGood = await ctx.CommittedSends
            .Where(cs => memberIds.Contains(cs.RecipientId) && cs.SentAtUtc.HasValue
                && !cs.IsDiagnostic && cs.StructuredReport != null)
            .OrderByDescending(cs => cs.ForecastSnapshot.GeneratedAtUtc)
            .Select(cs => new { cs.StructuredReport, cs.ForecastSnapshot })
            .FirstOrDefaultAsync(ct);

        ForecastSnapshotBody cachedBody;
        StructuredReportBody bandFree;
        string bandFreeJson;
        if (lastGood?.ForecastSnapshot is not { } cachedSnapshot
            || cachedSnapshot.SchemaVersion != ForecastSnapshotBody.SchemaVersionCurrent
            || lastGood.StructuredReport is null)
            return Skip("degraded on identical input and no usable cached snapshot");
        try
        {
            // Band-free: a cached scheduled re-send narrates no change (input unchanged
            // since the degrade), so strip the stale "What's changed" band before rendering.
            bandFree = WithoutChangeBand(StructuredReportBody.Deserialize(lastGood.StructuredReport));
            cachedBody = ForecastSnapshotBody.Deserialize(cachedSnapshot.Body);
            bandFreeJson = bandFree.Serialize();
        }
        catch (JsonException ex)
        {
            // A cached body that no longer deserializes must NOT defer the guaranteed
            // scheduled report. This is reachable across a release boundary when the body's
            // validation rules tighten — e.g. WX-189 forbade the {chN} anchors that older
            // persisted reports still carry. Fall through to a normal reconcile (null) so the
            // report is delivered on time and a fresh, current-schema body replaces the stale
            // one; the breaker self-heals (costs one Claude call this once, not a missed send).
            Logger.Warn($"{label}: WX-182 degrade circuit-breaker — cached report unusable ({ex.Message}); reconciling so the {reason} report is still delivered.");
            return null;
        }

        // Safety re-check (WX-182 acceptance: severe is unaffected). The breaker armed on a
        // non-hazard degrade, but a severe block fixed in the cached forecast can cross into
        // the hazard horizon as the clock advances on a stuck input. If one now falls within
        // it, do NOT serve a routine cached report — return null so the caller reconciles and
        // the WX-148 degrade-hazard path delivers the banner (safety over cost).
        if (SevereBlocks.AnyActiveWithin(cachedBody, now, DegradeHazardHorizon))
        {
            Logger.Info($"{label}: WX-182 degrade circuit-breaker — a severe block in the cached forecast is now within the hazard horizon; reconciling so the hazard path can send (not serving from cache).");
            return null;
        }

        Logger.Info($"{label}: WX-182 degrade circuit-breaker — re-issuing the {reason} report from the last good snapshot (no Claude call).");
        var plotsDir = new WxPaths(_config["InstallRoot"]).PlotsDir;
        var sent = 0;
        foreach (var member in members)
        {
            if (!servedIds.Contains(member.RecipientId))
                continue;  // brand-new members onboard on a normal reconciling cycle, not a cached re-send
            try
            {
                var langCode = ResolveLanguageCode(member, langById, cfg.DefaultLanguage);
                if (!TryResolveLanguage(langCode, member, label, out var templates, out var culture))
                    continue;  // incomplete language: this recipient fails closed; others proceed
                var innerBody = StructuredReportRenderer.Render(
                    bandFree, cachedBody, snapshot, member, templates, culture, tz, ReportKind.Scheduled, now);
                if (await DeliverWeatherReportAsync(
                        ctx, emailer, member, langCode, templates, culture, cachedSnapshot, innerBody,
                        bandFreeJson, reasoningTrace: "WX-182 cached re-send (degrade circuit-breaker); no Claude call",
                        snapshot, locality, tz, preferredIcaos, plotsDir, ReportKind.Scheduled, label,
                        cachedBody, ct))
                    sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to render or send cached scheduled report for {label}.", ex);
            }
        }

        if (sent > 0)
        {
            _degradeBreaker.Add(1, new KeyValuePair<string, object?>("outcome", "cached_send"));
            // clearDegraded:false — keep the breaker armed so further scheduled slots on this
            // same stuck input also serve from cache (zero Claude cost) until the input changes.
            await AdvanceBaselineAfterSendAsync(ctx, state, reason, now, inputHash, snapshot, label, ct, clearDegraded: false);
        }
        return sent;
    }

    /// <summary>
    /// WX-178/WX-193: the single "What's changed" band-strip decision shared by every send path
    /// that gates the band. Returns <paramref name="report"/> stripped (<see cref="WithoutChangeBand"/>)
    /// when <see cref="SuppressesScheduledChangeBand"/> says a scheduled or diagnostic report carries
    /// no near-term severe onset; otherwise returns it unchanged. Centralizing this is the deeper fix
    /// behind WX-193 — the regression existed because the strip lived inline in the scheduled path and
    /// was simply absent from the diagnostic one; one shared call keeps the two from drifting again. The
    /// prior baseline is deserialized only when <paramref name="kind"/> actually gates the band (an
    /// unscheduled cycle never needs it), and the suppression is logged with the kind for operability.
    /// </summary>
    private StructuredReportBody StripChangeBandUnlessSevereOnset(
        StructuredReportBody report, ForecastSnapshotBody finalSnapshot,
        ForecastSnapshot? priorSnapshot, ReportKind kind, DateTime nowUtc, TimeZoneInfo tz, string label)
    {
        // SuppressesScheduledChangeBand returns false for any non-gating kind; skip the prior
        // deserialize entirely for those rather than pay for a baseline the gate won't read.
        if (kind is not (ReportKind.Scheduled or ReportKind.Diagnostic)) return report;
        var priorBandBody = priorSnapshot is null ? null : ForecastSnapshotBody.Deserialize(priorSnapshot.Body);
        if (!SuppressesScheduledChangeBand(kind, report, finalSnapshot, priorBandBody, nowUtc, tz)) return report;
        Logger.Debug($"{label}: {kind} \"What's changed\" band suppressed — no near-term severe onset (WX-178/WX-193).");
        return WithoutChangeBand(report);
    }

    /// <summary>
    /// WX-182: a copy of <paramref name="report"/> with the "What's changed" band removed —
    /// empty <see cref="StructuredReportBody.Changes"/> and a null
    /// <see cref="NarrativeSections.ChangeSummary"/> in every language, keeping each
    /// language's closing. A cached scheduled re-send narrates no change (the input is
    /// unchanged since the degrade; scheduled reports narrate only severe-onset, which would
    /// have sent via the degrade-hazard path), so the stale band must not render.
    /// </summary>
    internal static StructuredReportBody WithoutChangeBand(StructuredReportBody report) =>
        report with
        {
            Changes = [],
            Narrative = report.Narrative.ToDictionary(
                kv => kv.Key, kv => kv.Value with { ChangeSummary = null }, StringComparer.Ordinal),
        };

    /// <summary>
    /// WX-178 (extended to Diagnostic by WX-193): whether a scheduled or diagnostic report's
    /// "What's changed" band must be suppressed. Such a report carries the band ONLY for a
    /// newly-appearing near-term severe hazard — a block going
    /// <see cref="ForecastSnapshotBlock.SevereFlag"/> false→true versus the prior committed snapshot,
    /// within local Day 1-3 (today through the day after tomorrow). For any other change the band
    /// reads as an unscheduled update and is stripped (<see cref="WithoutChangeBand"/>). Pure and
    /// deterministic — the reconciler prompt steers the LLM the same way and this is the backstop
    /// that guarantees it. Never suppresses for an unscheduled kind or a bandless report; a first
    /// send (no prior snapshot) that nonetheless carries a band IS suppressed — with no prior there
    /// is no onset to establish.  The horizon (local Day 1-3) is deliberately its own thing, not the
    /// <c>DegradeHazardHorizon</c> (~36 h, which governs sending a narrative-less hazard alert now).
    /// </summary>
    internal static bool SuppressesScheduledChangeBand(
        ReportKind kind, StructuredReportBody report, ForecastSnapshotBody finalSnapshot,
        ForecastSnapshotBody? priorBody, DateTime nowUtc, TimeZoneInfo tz)
    {
        if (kind is not (ReportKind.Scheduled or ReportKind.Diagnostic)) return false;
        if (!ReportCarriesChangeBand(report)) return false;   // nothing to suppress
        // First send (no prior): a severe ONSET can't be established against nothing, so a
        // "What's changed" band is meaningless — suppress it. (Severe still shows in the grid,
        // the WX-156 subject, and the closing.)
        if (priorBody is null) return true;
        return !finalSnapshot.HasSevereEscalationOver(priorBody, NearTermCutoffUtc(nowUtc, tz));
    }

    /// <summary>True when <paramref name="report"/> carries change content the band strip would clear — a non-empty changes array or any non-blank changeSummary. (The rendered band itself is driven by changeSummary; changes is included so a strip leaves a consistent band-free body. Validation keeps the two coupled.)</summary>
    private static bool ReportCarriesChangeBand(StructuredReportBody report) =>
        report.Changes.Count > 0
        || report.Narrative.Values.Any(n => !string.IsNullOrWhiteSpace(n.ChangeSummary));

    /// <summary>
    /// The UTC instant at the start of local Day 4 (today + 3 local days, at local midnight):
    /// a block starting before it falls on local Day 1-3, the WX-178 near-term window. DST-correct
    /// via <see cref="TimeZoneInfo"/>.
    /// </summary>
    private static DateTime NearTermCutoffUtc(DateTime nowUtc, TimeZoneInfo tz)
    {
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var day4LocalStart = DateTime.SpecifyKind(localNow.Date.AddDays(3), DateTimeKind.Unspecified);
        // WX-185 guard: in a midnight-transition timezone, local midnight on a spring-forward day
        // is a non-existent wall-clock time and ConvertTimeToUtc would throw, aborting the cycle.
        // Roll forward an hour (mirrors GfsSnapshotBuilder.FloorToLocalDayPartStart); a ~1 h shift
        // of a day-granular cutoff is immaterial to the near-term decision.
        if (tz.IsInvalidTime(day4LocalStart))
            day4LocalStart = day4LocalStart.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(day4LocalStart, tz);
    }

    /// <summary>
    /// Onboards never-contacted members of a locality whose cycle is NOT sending this
    /// pass (pre-filter skip / gap): each gets a standalone welcome-only email,
    /// anchored to the locality's most-recent delivered snapshot — no Claude call and
    /// no disturbance to the other members (WX-130).  A non-sending locality has sent
    /// before, so that baseline snapshot exists; if it somehow does not, the welcomes
    /// are deferred to the locality's next reconciling cycle (which welcomes them in
    /// the inner loop).  Returns the number of welcomes delivered.
    /// </summary>
    /// <summary>
    /// Delivers one rendered report to one served member: wraps the inner body,
    /// writes the <see cref="CommittedSend"/> audit row, inserts the meteogram,
    /// sends via SMTP, and stamps <see cref="CommittedSend.SentAtUtc"/> on success.
    /// Shared by the normal (WX-130) and the degraded (WX-148) send paths — the
    /// callers differ only in how <paramref name="innerBody"/> is rendered and
    /// whether <paramref name="structuredReportJson"/> is present (null on a
    /// degraded, narrative-less send). Returns true iff the email was accepted.
    /// </summary>
    private async Task<bool> DeliverWeatherReportAsync(
        WeatherDataContext ctx, SmtpSender emailer, Recipient member, string langCode, TemplateSet templates, CultureInfo culture,
        ForecastSnapshot reconciledSnapshot, string innerBody,
        string? structuredReportJson, string? reasoningTrace,
        WeatherSnapshot snapshot, Locality locality, TimeZoneInfo tz,
        IReadOnlyList<string> preferredIcaos, string plotsDir, ReportKind kind, string label,
        ForecastSnapshotBody finalBody, CancellationToken ct)
    {
        var report = WrapAsEmailHtml(innerBody, langCode, snapshot, tz);

        var committedSend = new CommittedSend
        {
            ForecastSnapshot = reconciledSnapshot,
            RecipientId = member.RecipientId,
            ReasoningTrace = reasoningTrace,
            StructuredReport = structuredReportJson,
            EmailBody = report,   // pre-meteogram, by the CommittedSend.EmailBody convention
            CreatedAtUtc = DateTime.UtcNow,
        };
        ctx.CommittedSends.Add(committedSend);
        await ctx.SaveChangesAsync(ct);

        var subject = BuildSubject(snapshot, templates, culture, tz, kind, recipientName: member.Name, severeBody: finalBody);
        var plainFallback = SnapshotDescriber.Describe(snapshot, tz, ToUnitPreferences(member));

        var meteogramPath = FindMeteogramAbbrevPath(
            preferredIcaos.Count > 0 ? preferredIcaos[0] : "", member.TempUnit, locality.Timezone, plotsDir);
        var htmlToSend = meteogramPath is not null
            ? InsertMeteogramImage(report)
            : report.Replace("<!--meteogram-->", "", StringComparison.Ordinal);
        IReadOnlyDictionary<string, string>? inlineImages = meteogramPath is not null
            ? new Dictionary<string, string> { ["meteogramAbbrev"] = meteogramPath }
            : null;

        var sent = await emailer.SendAsync(
            member.Email, subject, plainFallback,
            htmlBody: htmlToSend, inlineImages: inlineImages,
            toName: member.Name, ct: ct);

        if (!sent) { _sendFailures.Add(1); return false; }

        _reportsSent.Add(1);
        committedSend.SentAtUtc = DateTime.UtcNow;
        try { await ctx.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _statePersistFailures.Add(1);
            Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to persist CommittedSend.SentAtUtc Id={committedSend.Id} after a successful send.", ex);
        }
        Logger.Info($"{member.RecipientId} {member.Email} ({member.Name}): report sent ({label}).");
        return true;
    }

    private async Task<int> WelcomeNewMembersAsync(
        WeatherDataContext ctx, SmtpSender emailer, Locality locality, List<Recipient> newMembers,
        List<string> memberIds, TimeZoneInfo tz, IReadOnlyList<int> scheduledHours, ReportConfig cfg,
        IReadOnlyDictionary<long, Language> langById, CancellationToken ct)
    {
        var anchor = await ctx.CommittedSends
            .Where(cs => memberIds.Contains(cs.RecipientId) && cs.SentAtUtc.HasValue && !cs.IsDiagnostic)
            .Select(cs => cs.ForecastSnapshot)
            .OrderByDescending(s => s.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (anchor is null)
            return 0;   // brand-new locality: founders are welcomed on its first reconciling cycle

        var sent = 0;
        foreach (var member in newMembers)
        {
            try
            {
                var langCode = ResolveLanguageCode(member, langById, cfg.DefaultLanguage);
                if (!TryResolveLanguage(langCode, member, $"welcome '{locality.Name}'", out var templates, out var culture))
                    continue;  // incomplete language: this recipient fails closed; others proceed
                if (await SendWelcomeAsync(ctx, emailer, member, langCode, templates, culture, locality, anchor, tz, scheduledHours, ct))
                    sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to send welcome for locality '{locality.Name}'.", ex);
            }
        }
        return sent;
    }

    /// <summary>
    /// Renders and sends one recipient's standalone welcome-only email (no weather),
    /// records the delivery as a <see cref="CommittedSend"/> anchored to
    /// <paramref name="anchorSnapshot"/> (so the recipient counts as served next
    /// cycle) with null reasoning/structured-report, and returns whether the SMTP
    /// send succeeded.  The welcome carries no meteogram.
    /// </summary>
    private async Task<bool> SendWelcomeAsync(
        WeatherDataContext ctx, SmtpSender emailer, Recipient member, string langCode, TemplateSet templates, CultureInfo culture, Locality locality,
        ForecastSnapshot anchorSnapshot, TimeZoneInfo tz, IReadOnlyList<int> scheduledHours,
        CancellationToken ct)
    {
        var innerBody = StructuredReportRenderer.RenderWelcome(member, templates, culture, locality.Name, tz, scheduledHours);
        var report = WrapWelcomeAsEmailHtml(innerBody, langCode);

        var committedSend = new CommittedSend
        {
            ForecastSnapshot = anchorSnapshot,
            RecipientId = member.RecipientId,
            ReasoningTrace = null,
            StructuredReport = null,
            EmailBody = report,
            CreatedAtUtc = DateTime.UtcNow,
        };
        ctx.CommittedSends.Add(committedSend);
        await ctx.SaveChangesAsync(ct);

        var subject = $"{templates.Get(Tok.WelcomeSubject)} — {locality.Name}";
        var plainFallback = StructuredReportRenderer.WelcomePlainText(member, templates, culture, locality.Name, scheduledHours);

        var sent = await emailer.SendAsync(
            member.Email, subject, plainFallback,
            htmlBody: report, inlineImages: null, toName: member.Name, ct: ct);

        if (!sent) { _sendFailures.Add(1); return false; }

        _reportsSent.Add(1);
        committedSend.SentAtUtc = DateTime.UtcNow;
        try { await ctx.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _statePersistFailures.Add(1);
            Logger.Error($"{member.RecipientId} {member.Email} ({member.Name}): failed to persist CommittedSend.SentAtUtc Id={committedSend.Id} after a successful welcome send.", ex);
        }
        Logger.Info($"{member.RecipientId} {member.Email} ({member.Name}): welcome sent (locality '{locality.Name}').");
        return true;
    }

    // ── persistence helpers ───────────────────────────────────────────────────

    /// <summary>WX-114: per-call token-usage DEBUG line — surfaces each Claude call's input/output/cache token split in the text log (the OTel counters carry the same totals for the dashboards, but not per call). <paramref name="label"/> identifies the call (the locality for a reconciliation, or <c>translate:&lt;iso&gt;</c> for a WX-172 generation); <paramref name="context"/> names the call kind.</summary>
    private static void LogClaudeTokens(string label, TokenUsage tokens, string context) =>
        Logger.Debug($"{label}: Claude tokens [{context}] — in={tokens.InputTokens} out={tokens.OutputTokens} cache-read={tokens.CacheReadInputTokens} cache-write={tokens.CacheCreationInputTokens}.");

    /// <summary>Adds a Claude call's token usage to the four OTel cost counters in one place (input / output / cache-read / cache-write). Shared by reconciliation and WX-172 template generation.</summary>
    private void AddClaudeTokens(TokenUsage tokens)
    {
        _claudeInputTokens.Add(tokens.InputTokens);
        _claudeOutputTokens.Add(tokens.OutputTokens);
        _claudeCacheReadTokens.Add(tokens.CacheReadInputTokens);
        _claudeCacheCreationTokens.Add(tokens.CacheCreationInputTokens);
    }

    /// <summary>Projects a <see cref="Recipient"/> entity's unit columns into the <see cref="UnitPreferences"/> the plain-text fallback describer consumes.</summary>
    private static UnitPreferences ToUnitPreferences(Recipient r) => new()
    {
        Temperature = r.TempUnit,
        Pressure = r.PressureUnit,
        WindSpeed = r.WindSpeedUnit,
    };

    /// <summary>
    /// Shared admission-control suppressor for the two day-banded send-timing rate-limits
    /// (WX-181 post-send debounce, WX-157 pre-scheduled quiet window). Parses
    /// <paramref name="scheduleRaw"/> fail-closed (a malformed schedule is logged and the
    /// limiter disabled this cycle — never throws), runs <paramref name="decide"/>, and on a
    /// suppress decision bumps <paramref name="counter"/> (mode-tagged). In Enforce mode it
    /// persists the unsent cycle and returns <see langword="true"/> so the caller skips the
    /// Claude call; in Shadow mode it logs the would-suppress and returns
    /// <see langword="false"/> so the cycle proceeds. <paramref name="ticketLabel"/> drives the
    /// Debug-log prefix the §13 verify scripts grep ("WX-181 debounce", "WX-157 quiet window");
    /// <paramref name="configName"/> drives the fail-closed Warn ("invalid {configName}").
    /// </summary>
    private async Task<bool> ApplyDayBandedSuppressorAsync(
        WeatherDataContext ctx, LocalityState state, string inputHash, string label,
        SignificanceGateMode gateMode, string triggerType,
        string scheduleRaw, string configName, string ticketLabel, Counter<long> counter,
        Func<IReadOnlyList<DayBandedSchedule.Step>, (bool suppress, string reason)> decide,
        CancellationToken ct)
    {
        IReadOnlyList<DayBandedSchedule.Step> schedule;
        try
        {
            schedule = DayBandedSchedule.Parse(scheduleRaw);
        }
        catch (FormatException ex)
        {
            Logger.Warn($"{label}: invalid {configName} '{scheduleRaw}' — disabled this cycle: {ex.Message}");
            return false;
        }

        var (suppress, reason) = decide(schedule);
        if (!suppress)
            return false;

        bool enforce = gateMode == SignificanceGateMode.Enforce;
        counter.Add(1, new KeyValuePair<string, object?>("mode", enforce ? "enforce" : "shadow"));
        if (enforce)
        {
            Logger.Debug($"{label}: {ticketLabel} suppressed {triggerType} cycle — {reason}; Claude not called.");
            await PersistUnsentCycleAsync(ctx, label, state, inputHash, ct);
            return true;
        }
        Logger.Debug($"{label}: {ticketLabel} (shadow) WOULD suppress {triggerType} — {reason}; calling Claude anyway.");
        return false;
    }

    /// <summary>
    /// Persists the "called Claude but not sending this cycle" locality state shared
    /// by the WX-80 not-news gate, the WX-108 suppression backstop, and the WX-114
    /// significance gate: records the input hash on the <see cref="LocalityState"/>
    /// so an identical next cycle pre-filter-skips, and leaves the last-sent state
    /// untouched (the locality's delivered baseline is unchanged). Persistence
    /// errors are logged, not thrown — an unsent cycle must not crash the worker.
    /// </summary>
    private static async Task PersistUnsentCycleAsync(
        WeatherDataContext ctx, string label, LocalityState state, string inputHash, CancellationToken ct)
    {
        state.LastClaudeInputHash = inputHash;
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error($"{label}: failed to persist unsent-cycle state.", ex);
        }
    }

    /// <summary>
    /// Builds a per-recipient provisional <see cref="ForecastSnapshot"/> for
    /// the WX-78 audit row and the WX-79 reconciliation pass.  The body is the
    /// GFS-derived deterministic projection produced by
    /// <see cref="GfsSnapshotBuilder.Build"/> at the recipient's coordinates
    /// (WX-77).  When the recipient has no coordinates configured or no GFS
    /// data is available for the location, the body is empty (schema-version-1,
    /// no blocks); the auditable shape stays valid.
    /// </summary>
    /// <param name="stationIcao">ICAO of the METAR station the snapshot anchors against.</param>
    /// <param name="lat">Recipient latitude in decimal degrees North, or <see langword="null"/> when not configured.</param>
    /// <param name="lon">Recipient longitude in decimal degrees East (negative = West), or <see langword="null"/> when not configured.</param>
    /// <param name="tz">Locality timezone whose local day-parts anchor the GFS snapshot's 6-hour blocks (WX-155).</param>
    /// <param name="generatedAtUtc">UTC time stamp for the snapshot's unique-key column.</param>
    /// <param name="ct">Cancellation token propagated to the GFS hourly-forecast query.</param>
    /// <returns>An unattached <see cref="ForecastSnapshot"/> ready to be added to the <see cref="WeatherDataContext"/>.</returns>
    private async Task<ForecastSnapshot> BuildProvisionalForecastSnapshotAsync(
        string stationIcao, double? lat, double? lon, TimeZoneInfo tz, DateTime generatedAtUtc, CancellationToken ct)
    {
        GfsHourlyForecast? hourly = null;
        if (lat.HasValue && lon.HasValue)
        {
            hourly = await GfsInterpreter.GetHourlyForecastAsync(lat.Value, lon.Value, _dbOptions, ct);
        }

        var body = hourly is not null
            ? GfsSnapshotBuilder.Build(hourly, tz)
            : new ForecastSnapshotBody();

        return new ForecastSnapshot
        {
            StationIcao = stationIcao,
            GeneratedAtUtc = generatedAtUtc,
            SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
            Body = body.Serialize(),
        };
    }

    // ── email envelope ────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps the inner HTML body produced by the deterministic
    /// <see cref="StructuredReportRenderer"/> (WX-130 — no longer a Claude
    /// artifact) in the standard WxReport email envelope:
    /// <c>&lt;!DOCTYPE html&gt;</c>, language-tagged <c>&lt;html&gt;</c>, viewport
    /// meta, and the dark-blue footer line carrying observation/GFS/version
    /// metadata.  The result is the pre-meteogram HTML stored on
    /// <see cref="CommittedSend.EmailBody"/>; meteogram replacement happens
    /// immediately after, before SMTP send.
    /// </summary>
    /// <param name="innerBodyHtml">Inner content of the <c>&lt;body&gt;</c> tag from the renderer (no doctype, no html/body wrappers, no markdown).</param>
    /// <param name="langCode">Recipient's resolved ISO 639-1 code (WX-166), used directly as the <c>lang</c> attribute — the canonical language identity, not re-derived from a display name.</param>
    /// <param name="snapshot">Weather snapshot supplying station + observation time + GFS run for the footer line.</param>
    /// <param name="tz">Recipient's timezone (presently unused — footer timestamps are UTC by Paul's request — but reserved for future per-recipient footer localisation).</param>
    /// <returns>The fully wrapped HTML ready for meteogram replacement and SMTP delivery.</returns>
    private static string WrapAsEmailHtml(string innerBodyHtml, string langCode, WeatherSnapshot snapshot, TimeZoneInfo tz)
    {
        _ = tz;
        var footer = BuildFooterHtml(snapshot);

        return $"""
            <!DOCTYPE html>
            <html lang="{langCode}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            </head>
            <body style="margin:0;padding:16px;background:#f0f4f8;font-family:Arial,Helvetica,sans-serif;">
            {innerBodyHtml.Trim()}
            {footer}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Wraps a WX-130 welcome-only body (no weather) in the email envelope with a
    /// minimal footer — just the product version, no observation/GFS line and no
    /// meteogram, since a first-contact welcome carries no weather data.
    /// </summary>
    private static string WrapWelcomeAsEmailHtml(string innerBodyHtml, string langCode)
    {
        return $"""
            <!DOCTYPE html>
            <html lang="{langCode}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            </head>
            <body style="margin:0;padding:16px;background:#f0f4f8;font-family:Arial,Helvetica,sans-serif;">
            {innerBodyHtml.Trim()}
            <div style="max-width:600px;margin:0 auto;">
            <div style="background:#1a3a5c;color:#c8daea;font-size:12px;text-align:center;padding:10px 20px;border-radius:0 0 6px 6px;">
            HarderWare WxServices {WxPaths.ProductVersion}
            </div>
            </div>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Builds a dark-blue footer div containing the observation timestamp,
    /// station ICAO, GFS model run cycle, and product version.  Generated
    /// deterministically in C# so the data is always accurate and consistently
    /// formatted regardless of report language.  Ported from the pre-WX-79
    /// <c>ClaudeClient.BuildFooterHtml</c>.
    /// </summary>
    /// <param name="snap">Snapshot supplying station, observation time, and GFS run.</param>
    /// <returns>An HTML string for the footer div.</returns>
    private static string BuildFooterHtml(WeatherSnapshot snap)
    {
        var gfsPart = snap.GfsForecast is { } gfs
            ? $" &middot; GFS: {gfs.ModelRunUtc:yyyy-MM-dd HHmm}Z"
            : " &middot; GFS: n/a";

        var obsPart = snap.ObservationAvailable
            ? $"{snap.StationIcao}: {snap.ObservationTimeUtc:yyyy-MM-dd HHmm}Z"
            : "No current observation";

        var line = $"{obsPart}{gfsPart}"
                 + $" &middot; HarderWare WxServices {WxPaths.ProductVersion}";

        return $"""
            <div style="max-width:600px;margin:0 auto;">
            <!--meteogram-->
            <div style="background:#1a3a5c;color:#c8daea;font-size:12px;text-align:center;padding:10px 20px;border-radius:0 0 6px 6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
            {line}
            </div>
            </div>
            """;
    }

    // ── send-decision logic ───────────────────────────────────────────────────

    /// <summary>
    /// Determines whether to invoke Claude for a locality right now, and why
    /// (WX-130 — the decision is per locality; every member shares one schedule
    /// and baseline). Triggers are evaluated in priority order:
    /// <list type="number">
    ///   <item><b>first</b> — the locality has never sent any report. Always sends.</item>
    ///   <item><b>scheduled</b> — the daily scheduled hour has arrived in the
    ///   locality's timezone and no scheduled report has been sent in that slot.
    ///   Always sends; bypasses the pre-filter.</item>
    ///   <item><b>change</b> — an input (METAR/TAF/GFS) has advanced since the
    ///   last Claude call (the WX-80 pre-filter passed). Routes to the Claude
    ///   invalidation gate, which may still judge it not news.</item>
    /// </list>
    /// The minimum inter-send gap is enforced before either non-first trigger.
    /// Unlike the pre-WX-80 logic, significance is <em>not</em> decided here — the
    /// pre-filter only asks "has any input changed?"; "is it worth sending?" is
    /// Claude's call via <paramref name="state"/>'s gate.
    /// </summary>
    /// <param name="tz">The locality's timezone (WX-130), used to resolve the scheduled-slot comparison.</param>
    /// <param name="scheduledHours">The locality's parsed daily send hours (0–23), shared by all members (WX-133).</param>
    /// <param name="state">Per-locality persisted state: last send times and the last-Claude-call input hash.</param>
    /// <param name="inputIdentity">This cycle's raw input identity (observation time, TAF issuance, GFS run).</param>
    /// <param name="cfg">Report config providing the minimum inter-send gap.</param>
    /// <param name="nowUtc">The UTC clock time to use as "now" for all comparisons.</param>
    /// <returns>
    /// A tuple of (<c>send</c>, <c>reason</c>, <c>kind</c>, <c>allowSkip</c>).
    /// <c>reason</c> is <c>"first"</c>, <c>"scheduled"</c>, or <c>"change"</c> when
    /// sending; <c>"gap"</c> (rate-limited) or <c>"prefilter-skip"</c> (no input
    /// advanced) when not. <c>allowSkip</c> is <see langword="true"/> only for the
    /// <c>"change"</c> trigger, enabling Claude's "not news" gate; scheduled and
    /// first sends are guaranteed and never skippable.
    /// </returns>
    internal static (bool send, string reason, ReportKind kind, bool allowSkip) ShouldSend(
        TimeZoneInfo tz,
        IReadOnlyList<int> scheduledHours,
        LocalityState state,
        InputIdentity inputIdentity,
        ReportConfig cfg,
        DateTime nowUtc)
    {
        // Locality that has never sent — guaranteed introductory send on the first cycle.
        if (!state.LastScheduledSentUtc.HasValue && !state.LastUnscheduledSentUtc.HasValue)
            return (true, "first", ReportKind.Scheduled, false);

        // ── Scheduled send (WX-157: takes precedence) ─────────────────────────
        // Evaluated BEFORE the min-gap and bypassing the pre-filter, so a preceding
        // unscheduled send can never defer a due scheduled report — a "7 AM report"
        // arrives at 7, not up to MinGapMinutes later (the late-scheduled deferral bug).
        // The pre-scheduled quiet window (WX-157, in the gate stage) is what keeps
        // unscheduled churn from landing right before the slot in the first place.

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        // Find the most recently passed scheduled hour today (if any).
        // A send is due when the last passed hour's slot has not yet been served.
        var lastPassedHour = scheduledHours
            .Where(h => h <= localNow.Hour)
            .Cast<int?>()
            .LastOrDefault();

        if (lastPassedHour.HasValue)
        {
            var slotStartUtc = TimeZoneInfo.ConvertTimeToUtc(
                new DateTime(localNow.Year, localNow.Month, localNow.Day, lastPassedHour.Value, 0, 0), tz);

            var sentAfterSlotStart = state.LastScheduledSentUtc.HasValue
                && state.LastScheduledSentUtc.Value >= slotStartUtc;

            if (!sentAfterSlotStart)
                return (true, "scheduled", ReportKind.Scheduled, false);
        }

        // ── Minimum gap (unscheduled/arrival path only; scheduled is exempt above) ──

        var minGap = TimeSpan.FromMinutes(cfg.MinGapMinutes);

        // Last time any report was sent to this recipient.  Use Max (which
        // ignores nulls) rather than a nullable `>` ternary: `a > b` is false
        // whenever either operand is null, so the old ternary returned the null
        // side when only one timestamp was set — skipping the gap check after a
        // scheduled-only send and letting an arrival fire with no rate limit.
        var lastSentUtc = new[] { state.LastScheduledSentUtc, state.LastUnscheduledSentUtc }.Max();

        // Enforce minimum gap.  Note: LastClaudeInputHash is deliberately NOT
        // touched on this path — an input that advanced during the gap must still
        // read as "changed" once the gap clears, so the deferred send fires on the
        // next eligible cycle.  A future refactor must not move the hash update
        // into the gap path, or post-gap arrival sends would be silently swallowed
        // (covered by ShouldSendTests.PostGap_AdvancedInput_SendsOnceGapClears).
        if (lastSentUtc.HasValue && (nowUtc - lastSentUtc.Value) < minGap)
            return (false, "gap", ReportKind.Scheduled, false);

        // ── Arrival pre-filter (WX-80) ────────────────────────────────────────

        // Cheap C# gate: has any input advanced since the last Claude call? If
        // not, skip the call entirely. If so, route to the Claude invalidation
        // gate (allowSkip) — Claude decides whether the advance is news worth an
        // unscheduled email. This replaces the deleted observation-to-observation
        // fingerprint, which decided significance in C# and misfired on KDWH.
        var changed = inputIdentity.ChangedSourcesSince(state.LastClaudeInputHash);
        if (changed.Count == 0)
            return (false, "prefilter-skip", ReportKind.Scheduled, false);

        return (true, "change", ReportKind.Unscheduled, true);
    }

    /// <summary>
    /// WX-181 day-banded update debounce decision (pure). Returns true when a *significant*
    /// unscheduled change should be SUPPRESSED because its day-band's minimum gap has not
    /// elapsed since the last unscheduled send. Sends (returns false) when: the change is a
    /// not-severe→severe onset (<paramref name="gate"/>.SevereEntered — punches through), the
    /// change is uncharacterized (no <c>EarliestChangedDayLocal</c>, e.g. disjoint horizon),
    /// or the locality has never sent an unscheduled update. The <see cref="ReportConfig.MinGapMinutes"/>
    /// floor is enforced separately (ShouldSend), so this only adds the longer day-banded window.
    /// </summary>
    internal static bool ShouldDebounceUpdate(
        SignificanceResult gate, DateTime? lastUnscheduledSentUtc,
        IReadOnlyList<DayBandedSchedule.Step> schedule,
        DateTime nowUtc, TimeZoneInfo tz, out string reason)
    {
        reason = "";
        if (gate.SevereEntered) return false;
        if (gate.EarliestChangedDayLocal is not DateOnly changedDay) return false;
        if (lastUnscheduledSentUtc is not DateTime lastUnscheduled) return false;

        // 1-based day from the recipient's "today": today = 1, tomorrow = 2, … A change to a
        // past day (shouldn't occur — the gate only reports in-horizon blocks) floors at 1.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));
        int dayNumber = Math.Max(1, changedDay.DayNumber - today.DayNumber + 1);
        int minMinutes = DayBandedSchedule.MinMinutesForDay(schedule, dayNumber);

        var sinceLast = nowUtc - lastUnscheduled;
        if (sinceLast < TimeSpan.FromMinutes(minMinutes))
        {
            reason = $"change reaches local day {dayNumber} (min gap {minMinutes} min); last unscheduled send {sinceLast.TotalMinutes:0} min ago";
            return true;
        }
        return false;
    }

    /// <summary>
    /// WX-157 day-banded pre-scheduled quiet window decision (pure). Returns true when a
    /// *significant* unscheduled change should be SUPPRESSED because the next upcoming
    /// scheduled slot falls within the change's day-band quiet window — its content will
    /// ride the scheduled report (which renders "What's changed"). Sends (returns false)
    /// when: the change is a not-severe→severe onset (<paramref name="gate"/>.SevereEntered
    /// — punches through), the change is uncharacterized (no <c>EarliestChangedDayLocal</c>,
    /// e.g. disjoint horizon), the locality has no scheduled hours, or the next slot is
    /// further off than the quiet window. The scheduled report itself is never quieted
    /// (ShouldSend fires it on its slot); <see cref="ReportConfig.MinGapMinutes"/> remains a
    /// hard floor. Parallels <see cref="ShouldDebounceUpdate"/>.
    /// </summary>
    internal static bool ShouldQuietBeforeSlot(
        SignificanceResult gate, IReadOnlyList<int> scheduledHours,
        IReadOnlyList<DayBandedSchedule.Step> schedule,
        DateTime nowUtc, TimeZoneInfo tz, out string reason)
    {
        reason = "";
        if (gate.SevereEntered) return false;
        if (gate.EarliestChangedDayLocal is not DateOnly changedDay) return false;
        if (scheduledHours.Count == 0) return false;

        // Nearest upcoming scheduled slot (strictly after now), handling multiple hours,
        // day rollover, and tz/DST via ConvertTimeToUtc. Scheduled hours are ascending
        // (ScheduledSendHoursFormat.Parse), matching ShouldSend's lastPassedHour scan.
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        DateTime nextSlotLocal = default;
        bool found = false;
        foreach (var h in scheduledHours)
        {
            var slot = new DateTime(localNow.Year, localNow.Month, localNow.Day, h, 0, 0, DateTimeKind.Unspecified);
            if (slot > localNow) { nextSlotLocal = slot; found = true; break; }
        }
        if (!found)
        {
            // All of today's slots have passed — the next is the earliest hour tomorrow.
            var firstHour = scheduledHours[0];
            nextSlotLocal = new DateTime(localNow.Year, localNow.Month, localNow.Day, firstHour, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        }
        var nextSlotUtc = TimeZoneInfo.ConvertTimeToUtc(nextSlotLocal, tz);
        var untilSlot = nextSlotUtc - nowUtc;

        // 1-based day from the recipient's "today": today = 1, tomorrow = 2, …
        var today = DateOnly.FromDateTime(localNow);
        int dayNumber = Math.Max(1, changedDay.DayNumber - today.DayNumber + 1);
        int quietMinutes = DayBandedSchedule.MinMinutesForDay(schedule, dayNumber);

        if (untilSlot <= TimeSpan.FromMinutes(quietMinutes))
        {
            reason = $"next scheduled slot in {untilSlot.TotalMinutes:0} min <= quiet window {quietMinutes} min (change reaches local day {dayNumber}); content rides the scheduled report";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Outcome of the WX-108 deterministic post-reconciliation backstop for an
    /// unscheduled cycle.  Pure function of the snapshots and the data vintage —
    /// no significance judgment, which remains Claude's job.
    /// </summary>
    internal enum UnscheduledSuppression
    {
        /// <summary>Send the report — it is materially new news.</summary>
        None,

        /// <summary>Suppress: the reconciled snapshot is materially identical to the last sent report.</summary>
        Redundant,

        /// <summary>Suppress: the only material change is a severe-flag *de-escalation* on an observation-only advance with no newer GFS run or TAF. A severe *escalation* is never suppressed — it is news.</summary>
        SevereFlip,
    }

    /// <summary>
    /// Decides whether an unscheduled, successfully-reconciled send should be
    /// suppressed as a redundant re-send or an untrusted observation-driven
    /// severe-flag flip (WX-108).  Extracted from <see cref="RunCycleAsync"/> so the
    /// decision is unit-testable in isolation, like <see cref="ShouldSend"/>.
    /// </summary>
    /// <param name="priorBody">The last <em>sent</em> snapshot body for the recipient (the committed anchor).</param>
    /// <param name="finalBody">The freshly reconciled snapshot body Claude returned this cycle.</param>
    /// <param name="freshGuidanceSinceLastSend">True when a newer GFS run or TAF issuance has arrived since the last sent report; a severe-flag flip is trusted only then.</param>
    /// <returns>
    /// <see cref="UnscheduledSuppression.Redundant"/> when the bodies are materially
    /// equal; <see cref="UnscheduledSuppression.SevereFlip"/> when they differ only by
    /// severe flags, no fresh guidance supports the flip, and the flip is a
    /// de-escalation (no new severe hazard appears); otherwise
    /// <see cref="UnscheduledSuppression.None"/>.
    /// </returns>
    internal static UnscheduledSuppression EvaluateUnscheduledSuppression(
        ForecastSnapshotBody priorBody, ForecastSnapshotBody finalBody, bool freshGuidanceSinceLastSend)
    {
        if (priorBody.MateriallyEquals(finalBody))
            return UnscheduledSuppression.Redundant;
        // Severe hysteresis is one-directional: suppress a de-escalation on an
        // observation-only advance (the untrusted whipsaw), but never the arrival
        // of a new severe hazard — that is always news (directional asymmetry).
        if (!freshGuidanceSinceLastSend
            && priorBody.MateriallyEqualsIgnoringSevere(finalBody)
            && !finalBody.HasSevereEscalationOver(priorBody))
            return UnscheduledSuppression.SevereFlip;
        return UnscheduledSuppression.None;
    }

    /// <summary>
    /// Maps the set of inputs that advanced this cycle to a single low-cardinality
    /// telemetry label for the <c>wxreport.triggers.total</c> counter's
    /// <c>trigger.type</c> tag: the lone source's name when exactly one advanced,
    /// <c>"multiple"</c> when several did, or <c>"unknown"</c> for the (not
    /// expected on a change trigger) empty set.
    /// </summary>
    /// <param name="changed">The sources whose identity advanced since the last Claude call.</param>
    /// <returns>A stable label: <c>"metar"</c>, <c>"taf"</c>, <c>"gfs"</c>, <c>"multiple"</c>, or <c>"unknown"</c>.</returns>
    private static string ArrivalLabel(IReadOnlyList<TriggerSource> changed) => changed.Count switch
    {
        0 => "unknown",
        1 => changed[0] switch
        {
            TriggerSource.Metar => "metar",
            TriggerSource.Taf => "taf",
            TriggerSource.Gfs => "gfs",
            _ => "unknown",
        },
        _ => "multiple",
    };

    /// <summary>
    /// Resolves an IANA or Windows timezone ID to a <see cref="TimeZoneInfo"/>.
    /// Falls back to UTC if the ID is unrecognised — callers should validate
    /// config values at startup to avoid silent wrong-timezone sends.
    /// </summary>
    /// <param name="id">IANA or Windows timezone identifier (e.g. <c>"America/Chicago"</c>).</param>
    /// <returns>
    /// The matching <see cref="TimeZoneInfo"/>, or <see cref="TimeZoneInfo.Utc"/>
    /// if the ID cannot be found on the current system.
    /// </returns>
    private static TimeZoneInfo ResolveTimezone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            Logger.Warn($"Timezone '{id}' was not recognised on this system — falling back to UTC. Check the Timezone setting in WxManager → Recipients.");
            return TimeZoneInfo.Utc;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the recipient language's <see cref="TemplateSet"/> and <see cref="CultureInfo"/>,
    /// applying the WX-171 per-recipient fail-closed send gate: if the language is missing any
    /// renderer-required token (<see cref="Tok.All"/>), logs an ERROR (which WxMonitor alerts on)
    /// and returns <see langword="false"/> so THIS recipient is skipped this cycle — every other
    /// recipient and language still sends. A complete language always returns <see langword="true"/>;
    /// completeness is verified here, before the renderer's <see cref="TemplateSet.Get"/> can throw.
    /// </summary>
    private bool TryResolveLanguage(string langCode, Recipient member, string label,
        out TemplateSet templates, out CultureInfo culture)
    {
        templates = _templates.ForLanguage(langCode);
        culture = _templates.CultureFor(langCode);
        var missing = _templates.MissingTokens(langCode, Tok.All);
        if (missing.Count == 0)
            return true;

        Logger.Error(
            $"{label}: language '{langCode}' is incomplete — {missing.Count} renderer template token(s) missing " +
            $"([{string.Join(", ", missing.Take(15))}{(missing.Count > 15 ? ", …" : "")}]). " +
            $"Recipient {member.RecipientId} ({member.Email}) gets no report this cycle; other recipients and languages are unaffected. " +
            "(WX-171 fail-closed send gate; resolve by regenerating/repairing the language's templates.)");
        return false;
    }

    /// <summary>
    /// Builds a localised email subject line for a weather report.
    /// The subject includes the recipient's first name, the locality name, and the local observation time.
    /// The subject word is chosen from <paramref name="kind"/> — the same source as
    /// the rendered header label (WX-154), so subject and label cannot disagree —
    /// both read the kind→token mapping from <see cref="ReportLabels.TokenFor"/>.
    /// </summary>
    /// <param name="snap">Snapshot providing the station ICAO, locality name, and observation time.</param>
    /// <param name="templates">The recipient-language template set supplying the localized subject words.</param>
    /// <param name="culture">The recipient's culture, used to format the subject time so it matches the body and stays deterministic across service-account/host cultures (WX-171).</param>
    /// <param name="tz">Timezone used to convert the UTC observation time for display.</param>
    /// <param name="kind">
    /// The kind of send: <see cref="ReportKind.Unscheduled"/> → "Weather Update",
    /// <see cref="ReportKind.Diagnostic"/> → "Diagnostic", and
    /// <see cref="ReportKind.Scheduled"/> → "Weather Report".
    /// </param>
    /// <param name="recipientName">Display name of the recipient, included in the subject (e.g. <c>"Paul"</c>).</param>
    /// <param name="severeBody">When provided, the reconciled forecast snapshot is inspected for a near-term severe
    /// hazard (WX-156): if the rule holds, a front-loaded severe noun is prepended to the subject. Null skips the check.</param>
    /// <returns>A localised subject string, e.g. <c>"Weather Report for Paul — The Woodlands (7:05 AM)"</c>,
    /// or with a WX-156 hazard prefix <c>"Severe storms — Weather Update for Paul — The Woodlands (7:05 AM)"</c>.</returns>
    private static string BuildSubject(WeatherSnapshot snap, TemplateSet templates, CultureInfo culture, TimeZoneInfo tz,
        ReportKind kind, string recipientName = "", ForecastSnapshotBody? severeBody = null)
    {
        // Use send-time when no observation is available; ObservationTimeUtc is
        // default(DateTime) in that case, which would render as 12:00 AM.
        var subjectTimeUtc = snap.ObservationAvailable ? snap.ObservationTimeUtc : DateTime.UtcNow;
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(subjectTimeUtc, tz).ToString("h:mm tt", culture);

        var label = templates.Get(ReportLabels.TokenFor(kind, LabelType.Title));
        var forName = string.IsNullOrWhiteSpace(recipientName)
            ? ""
            : $" {templates.Get(Tok.SubjectForConnective)} {recipientName}";
        var subject = $"{label}{forName} — {snap.LocalityName} ({localTime})";

        // WX-156: front-load a severe-weather noun (mobile truncation) when one is in the next 24 h.
        var hazardPrefix = severeBody is null ? null : SevereSubjectPrefix.Evaluate(severeBody, DateTime.UtcNow, templates);
        return hazardPrefix is null ? subject : $"{hazardPrefix} — {subject}";
    }

    /// <summary>
    /// Writes the current UTC timestamp to the heartbeat file so that WxMonitor
    /// can confirm this service is still running.  Does nothing if
    /// <paramref name="path"/> is null or whitespace.
    /// </summary>
    /// <param name="path">Absolute path to the heartbeat file, or <see langword="null"/> to skip.</param>
    /// <sideeffects>Creates or overwrites the file at <paramref name="path"/> with an ISO 8601 UTC timestamp.</sideeffects>
    private static void WriteHeartbeat(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Logger.Warn($"Could not write heartbeat to '{path}': {ex.Message}"); }
    }

    /// <summary>
    /// Parses a comma-separated list of ICAO station identifiers into a list of
    /// trimmed, non-empty strings.
    /// </summary>
    /// <param name="raw">Comma-separated ICAO string from config (e.g. <c>"KDWH, KHOU"</c>), or <see langword="null"/>.</param>
    /// <returns>
    /// An ordered list of trimmed ICAO strings, or an empty list if
    /// <paramref name="raw"/> is null or whitespace.
    /// </returns>
    private static IReadOnlyList<string> ParseIcaoList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(s => !string.IsNullOrWhiteSpace(s))
                 .ToList();

    /// <summary>
    /// Parses a comma-separated string of scheduled send hours into a sorted list of valid hour values (0–23).
    /// Entries that cannot be parsed or are out of range are silently ignored.
    /// Delegates to the shared <see cref="ScheduledSendHoursFormat"/> (WX-127) so the
    /// runtime and the WxManager editors share one format contract.
    /// </summary>
    private static IReadOnlyList<int> ParseHourList(string? raw) =>
        ScheduledSendHoursFormat.Parse(raw);

    /// <summary>
    /// Loads and returns the current configuration.  Recipients are loaded from the
    /// <c>Recipients</c> table; if the table is empty, the <c>Report:Recipients</c>
    /// config section is used as a fallback.  Non-secret settings (intervals,
    /// thresholds, SMTP host/port) come from config files.  Secrets (SMTP credentials,
    /// Claude API key) are read exclusively from <see cref="GlobalSettings"/> (Id = 1).
    /// </summary>
    private async Task<(ReportConfig report, SmtpConfig smtp, ClaudeConfig claude)> LoadConfigsAsync(
        WeatherDataContext ctx, CancellationToken ct)
    {
        var report = new ReportConfig();
        _config.GetSection("Report").Bind(report);
        report.HeartbeatFile ??= new WxPaths(_config["InstallRoot"]).HeartbeatFile("wxreport");

        var dbRecipients = await ctx.Recipients.OrderBy(r => r.Id).ToListAsync(ct);
        if (dbRecipients.Count > 0)
        {
            var langById = await ctx.Languages.ToDictionaryAsync(l => l.Id, ct);
            report.Recipients = dbRecipients
                .Select(r => ToConfig(r, r.LanguageId is long id && langById.TryGetValue(id, out var l) ? l.DisplayName : null))
                .ToList();
        }

        var smtp = new SmtpConfig();
        _config.GetSection("Smtp").Bind(smtp);

        var claude = new ClaudeConfig();
        _config.GetSection("Claude").Bind(claude);

        var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        smtp.Username = gs?.SmtpUsername ?? "";
        smtp.Password = gs?.SmtpPassword ?? "";
        smtp.FromAddress = gs?.SmtpFromAddress ?? "";
        claude.ApiKey = gs?.ClaudeApiKey ?? "";

        return (report, smtp, claude);
    }

    // ── meteogram helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Searches the most recent meteogram manifest in <paramref name="plotsDir"/>
    /// for the 24-hour PNG file matching the given <paramref name="icao"/>,
    /// <paramref name="tempUnit"/>, and <paramref name="timezone"/>.
    /// Returns the full path to the PNG if found and the file exists; otherwise
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="icao">ICAO station identifier to look up in the manifest.</param>
    /// <param name="tempUnit">Temperature unit (<c>"F"</c> or <c>"C"</c>) to match.</param>
    /// <param name="timezone">IANA timezone name to match (e.g. <c>"America/Chicago"</c>).</param>
    /// <param name="plotsDir">Directory where WxVis.Svc writes PNGs and manifest files.</param>
    private static string? FindMeteogramAbbrevPath(string icao, string tempUnit, string timezone, string plotsDir)
    {
        if (string.IsNullOrWhiteSpace(icao) || !Directory.Exists(plotsDir))
            return null;

        var manifests = Directory.GetFiles(plotsDir, "meteogram_manifest_*.json")
            .OrderByDescending(f => f)
            .ToList();

        foreach (var manifestPath in manifests)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("Icao", out var icaoProp)) continue;
                    if (!entry.TryGetProperty("TempUnit", out var tuProp)) continue;
                    if (!entry.TryGetProperty("Timezone", out var tzProp)) continue;
                    if (!entry.TryGetProperty("FileAbbrev", out var fileAbbrevProp)) continue;

                    if (!string.Equals(icaoProp.GetString(), icao, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(tuProp.GetString(), tempUnit, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(tzProp.GetString(), timezone, StringComparison.Ordinal)) continue;

                    var file = fileAbbrevProp.GetString();
                    if (string.IsNullOrWhiteSpace(file)) continue;

                    var fullPath = Path.Combine(plotsDir, file);
                    if (File.Exists(fullPath)) return fullPath;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"FindMeteogramAbbrevPath: could not read manifest '{manifestPath}': {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Replaces the <c>&lt;!--meteogram--&gt;</c> sentinel (placed inside the footer
    /// wrapper by <c>ClaudeClient.BuildFooterHtml</c>) with a centred meteogram image tag,
    /// keeping the image structurally inside the footer's outer div.
    /// Falls back to inserting before <c>&lt;/body&gt;</c> if the sentinel is absent.
    /// </summary>
    /// <param name="html">Claude-generated HTML report containing the sentinel.</param>
    /// <returns>Modified HTML string with the meteogram image included.</returns>
    private static string InsertMeteogramImage(string html)
    {
        const string img =
            "<p style=\"text-align:center;margin-top:16px\">" +
            "<img src=\"cid:meteogramAbbrev\" style=\"width:100%;max-width:1000px\" " +
            "alt=\"48-hour forecast meteogram\"><br>" +
            "<span style=\"font-size:11px;color:#888;font-style:italic\">" +
            "Forecast of temperature, humidity, and wind over time. " +
            "Wind symbols point in the direction the wind is blowing, " +
            "with more feathers indicating stronger winds." +
            "</span>" +
            "</p>";

        const string sentinel = "<!--meteogram-->";
        var idx = html.IndexOf(sentinel, StringComparison.Ordinal);
        if (idx >= 0)
            return html[..idx] + img + html[(idx + sentinel.Length)..];

        // Fallback: no sentinel found — insert before </body>.
        var bodyIdx = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyIdx >= 0
            ? html[..bodyIdx] + img + html[bodyIdx..]
            : html + img;
    }

    /// <summary>
    /// Resolves a recipient's <see cref="Recipient.LanguageId"/> FK to its ISO 639-1
    /// code via the cycle's <paramref name="langById"/> map (the codebase's dict-lookup
    /// FK convention — no navigation load). A null FK (or a dangling id) falls back to
    /// the service <paramref name="defaultName"/>, converted once.
    /// <para>
    /// The code is the canonical internal language identity (WX-166): every lookup,
    /// narrative/vocabulary selection, the <c>html lang</c> attribute, and the subject
    /// key on it; <see cref="Language.DisplayName"/> is for human display only. The lone
    /// name→code conversion is this default bootstrap, because the service default is a
    /// configured name. Passing the resolved code (not the entity) keeps every consumer
    /// compile-checked: a missed site is a build error, never a silent English default.
    /// </para>
    /// </summary>
    private static string ResolveLanguageCode(
        Recipient member, IReadOnlyDictionary<long, Language> langById, string defaultName)
        => member.LanguageId is long id && langById.TryGetValue(id, out var lang)
            ? lang.IsoCode
            : LanguageHelper.ToIetfTag(defaultName);

    /// <summary>
    /// Converts a <see cref="Recipient"/> database entity to the <see cref="RecipientConfig"/>
    /// view model used throughout the report worker. <paramref name="languageName"/> is
    /// the recipient's resolved language display name (WX-166), or <see langword="null"/>
    /// when the recipient uses the service default.
    /// </summary>
    /// <param name="r">The database recipient entity to convert.</param>
    /// <param name="languageName">Resolved language display name, or <see langword="null"/> for the service default.</param>
    /// <returns>A populated <see cref="RecipientConfig"/> instance.</returns>
    private static RecipientConfig ToConfig(Recipient r, string? languageName) => new()
    {
        Id = r.RecipientId,
        Email = r.Email,
        Name = r.Name,
        Language = languageName,
        Timezone = r.Timezone,
        ScheduledSendHours = r.ScheduledSendHours,
        Address = r.Address,
        LocalityName = r.LocalityName,
        LocalityId = r.LocalityId,
        Latitude = r.Latitude,
        Longitude = r.Longitude,
        MetarIcao = r.MetarIcao,
        TafIcao = r.TafIcao,
        Units = new UnitPreferences
        {
            Temperature = r.TempUnit,
            Pressure = r.PressureUnit,
            WindSpeed = r.WindSpeedUnit,
        },
    };
}