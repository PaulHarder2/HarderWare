using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    private readonly HttpClient _httpClient;
    private readonly HttpClient _claudeHttpClient;
    private readonly PersonaPrefix _persona;

    private readonly Meter _meter = new("WxReport.Svc", "1.0.0");
    private readonly Counter<long> _reportCycles;
    private readonly Counter<long> _reportsSent;
    private readonly Counter<long> _sendFailures;
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
    private readonly Histogram<double> _cycleDuration;
    private readonly Histogram<double> _claudeDuration;

    /// <summary>Initializes a new instance of <see cref="ReportWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to load the <c>Report</c> config section each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> to read/write recipient state.</param>
    /// <param name="httpClientFactory">Factory for the named <c>WxReport</c> client (geocoding/airport lookups, 100s default timeout) and the <c>Claude</c> client (reconciliation, long timeout per WX-100).</param>
    /// <param name="persona">Author-persona prefix loaded once at startup and threaded into every Claude call.</param>
    public ReportWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions,
        IHttpClientFactory httpClientFactory,
        PersonaPrefix persona)
    {
        _config = config;
        _dbOptions = dbOptions;
        _httpClient = httpClientFactory.CreateClient("WxReport");
        // Separate client for Claude: its long reconciliation timeout (WX-100)
        // must not bleed into the fast-fail geocoding/airport calls on _httpClient.
        _claudeHttpClient = httpClientFactory.CreateClient("Claude");
        _persona = persona;
        _reportCycles = _meter.CreateCounter<long>("wxreport.cycles.total", description: "Number of completed report cycles.");
        _reportsSent = _meter.CreateCounter<long>("wxreport.sends.total", description: "Number of reports successfully sent.");
        _sendFailures = _meter.CreateCounter<long>("wxreport.send.failures.total", description: "Number of failed email sends.");
        _claudeCalls = _meter.CreateCounter<long>("wxreport.claude.calls.total", description: "Number of Claude API calls.");
        _claudeInputTokens = _meter.CreateCounter<long>("wxreport.claude.tokens.input.total", description: "Total billed input tokens (cached + uncached) across reconciliation calls.");
        _claudeOutputTokens = _meter.CreateCounter<long>("wxreport.claude.tokens.output.total", description: "Total output tokens generated across reconciliation calls.");
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
    /// Sends an immediate out-of-cycle weather report to the first valid recipient
    /// in the configuration.  Called once at service startup so that a deployment
    /// can be verified without waiting for the scheduled send hour.
    /// All send-decision logic is bypassed; the report is sent unconditionally
    /// as long as METAR data is available.  Recipient state is updated with an
    /// unscheduled timestamp so the minimum inter-send gap applies to the next
    /// normal cycle, preventing an immediate duplicate.
    /// </summary>
    /// <param name="ct">Cancellation token propagated to database and HTTP operations.</param>
    /// <sideeffects>
    /// Sends one email via SMTP.
    /// Makes HTTP calls to the Claude API and, if the recipient is not yet
    /// resolved, to geocoding and airport-lookup APIs.
    /// Reads and writes <see cref="RecipientState"/> in the database.
    /// Writes log entries for each significant step.
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

        // Try candidates in order until one resolves — a persistent resolve
        // failure on the first recipient (e.g. a locality member whose locality
        // has no stations yet, WX-127) must not silence the startup report for
        // every deployment.
        var resolver = new RecipientResolver(_dbOptions, _httpClient, _config["What3Words:ApiKey"]);
        RecipientConfig? recipient = null;
        foreach (var candidate in cfg.Recipients.Where(r =>
                     !string.IsNullOrWhiteSpace(r.Email) && !string.IsNullOrWhiteSpace(r.Id)))
        {
            if (await resolver.EnsureResolvedAsync(candidate)) { recipient = candidate; break; }
        }

        if (recipient is null)
        {
            Logger.Debug("No resolvable recipient found for startup report.");
            return;
        }

        var preferredIcaos = ParseIcaoList(recipient.MetarIcao);
        var localityName = recipient.LocalityName ?? (preferredIcaos.Count > 0 ? preferredIcaos[0] : recipient.Email);
        var snapshot = await WxInterpreter.GetSnapshotAsync(
            preferredIcaos, recipient.TafIcao == "NONE" ? null : recipient.TafIcao, localityName, _dbOptions,
            homeLat: recipient.Latitude, homeLon: recipient.Longitude,
            precipThresholdMmHr: cfg.PrecipRateThresholdMmHr,
            ct: ct);

        if (snapshot is null)
        {
            Logger.Warn($"{recipient.Id} {recipient.Email} ({recipient.Name}): no METAR data for startup report ({recipient.MetarIcao}) — skipping.");
            return;
        }

        Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): sending startup report.");

        var language = recipient.Language ?? cfg.DefaultLanguage;
        var scheduledHours = ParseHourList(recipient.ScheduledSendHours ?? cfg.DefaultScheduledSendHours);
        var scheduledHour = scheduledHours.Count > 0 ? scheduledHours[0] : 7;
        var tz = ResolveTimezone(recipient.Timezone);

        // WX-86: write the provisional CommittedSend before invoking Claude,
        // mirroring RunCycleAsync.  A Claude failure still leaves an audit
        // row showing what we were about to send for the startup path.
        // Anchor key falls back to the TAF station when the METAR station is
        // empty (observationless-snapshot path), so anchor rows never carry an
        // empty key (WX-79 CR finding).
        var snapshotKey = !string.IsNullOrWhiteSpace(snapshot.StationIcao)
            ? snapshot.StationIcao
            : (snapshot.TafStationIcao ?? "");
        var anchorSnapshot = await BuildProvisionalForecastSnapshotAsync(
            snapshotKey, recipient.Latitude, recipient.Longitude, DateTime.UtcNow, ct);
        ctx.ForecastSnapshots.Add(anchorSnapshot);
        var committedSend = new CommittedSend
        {
            ForecastSnapshot = anchorSnapshot,
            RecipientId = recipient.Id!,
            CreatedAtUtc = DateTime.UtcNow,
        };
        ctx.CommittedSends.Add(committedSend);
        await ctx.SaveChangesAsync(ct);
        Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): wrote provisional CommittedSend Id={committedSend.Id}.");

        var reconciler = new ForecastReconciler(
            new ClaudeClient(_claudeHttpClient, claude_cfg.ApiKey, claude_cfg.Model, _persona.Text));

        // Prior-snapshot lookup is recipient-keyed via CommittedSends so two
        // recipients sharing a station don't cross-contaminate each other's
        // reconciliation context.  SentAtUtc.HasValue restricts to snapshots
        // we actually delivered — Claude-failed audit rows don't count as
        // priors (WX-79 CR finding).
        var priorSnapshot = await ctx.CommittedSends
            .Where(cs => cs.RecipientId == recipient.Id && cs.SentAtUtc.HasValue)
            .Select(cs => cs.ForecastSnapshot)
            .Where(s => s.Id != anchorSnapshot.Id)
            .OrderByDescending(s => s.GeneratedAtUtc)
            .FirstOrDefaultAsync(ct);

        var provisionalBody = ForecastSnapshotBody.Deserialize(anchorSnapshot.Body);

        var reconcileResult = await reconciler.ReconcileAsync(
            snapshot, provisionalBody,
            snapshot.GfsForecast?.ModelRunUtc,
            snapshot.TafIssuanceUtc, snapshot.TafValidToUtc,
            priorSnapshot,
            language,
            // WX-128 additive transition: single-recipient language; see RunCycleAsync.
            narrativeLanguages: new[] { LanguageHelper.ToIetfTag(language) },
            recipient.Name, tz,
            isFirstReport: false,
            scheduledHour: scheduledHour,
            units: recipient.Units,
            changeSeverity: ChangeSeverity.None,
            previousMetarIcao: null,
            allowSkip: false, // startup is an unconditional verification send — never skippable
            changedSinceLastSend: Array.Empty<TriggerSource>(), // unused on a guaranteed send
            ct: ct);

        // Startup passes allowSkip:false, so ReconcileAsync never returns NotNews
        // here — a stray skip_send is converted to Failure at the reconciler and
        // handled by this branch (logged, provisional left in place).
        if (reconcileResult is not ReconcileResult.Success success)
        {
            _claudeMalformedOutput.Add(1);
            var reason = ((ReconcileResult.Failure)reconcileResult).Reason;
            Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): Reconciliation failed for startup send: {reason}.  Provisional CommittedSend Id={committedSend.Id} left in place.");
            return;
        }

        _claudeToolUseSuccess.Add(1);
        _claudeInputTokens.Add(success.Tokens.InputTokens);
        _claudeOutputTokens.Add(success.Tokens.OutputTokens);
        _claudeCacheReadTokens.Add(success.Tokens.CacheReadInputTokens);
        _claudeCacheCreationTokens.Add(success.Tokens.CacheCreationInputTokens);
        LogClaudeTokens(recipient, success.Tokens, "startup");

        // WX-79: persist the reconciled snapshot row and re-anchor the
        // CommittedSend.  EmailBody is the pre-meteogram wrapped HTML
        // (matches the CommittedSend.EmailBody = Claude artifact convention);
        // ReasoningTrace is the audit log Claude produced.
        var reconciledSnapshot = new ForecastSnapshot
        {
            StationIcao = snapshot.StationIcao,
            GeneratedAtUtc = DateTime.UtcNow,
            SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
            Body = success.FinalSnapshot.Serialize(),
        };
        ctx.ForecastSnapshots.Add(reconciledSnapshot);
        committedSend.ForecastSnapshot = reconciledSnapshot;
        var report = WrapAsEmailHtml(success.EmailBody, language, snapshot, tz);
        committedSend.EmailBody = report;
        committedSend.ReasoningTrace = success.ReasoningTrace;
        // WX-128: persist the unit-neutral structured report alongside the email
        // body. Unread in the additive transition; WX-129's renderer consumes it.
        committedSend.StructuredReport = success.StructuredReport.Serialize();
        await ctx.SaveChangesAsync(ct);
        Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): reconciled CommittedSend Id={committedSend.Id} → ForecastSnapshot Id={reconciledSnapshot.Id}.");

        var subject = BuildSubject(snapshot, language, tz, recipientName: recipient.Name);
        var plainFallback = SnapshotDescriber.Describe(snapshot, tz, recipient.Units);

        var plotsDir = new WxPaths(_config["InstallRoot"]).PlotsDir;
        var meteogramPath = FindMeteogramAbbrevPath(preferredIcaos.Count > 0 ? preferredIcaos[0] : "", recipient.Units.Temperature, recipient.Timezone, plotsDir);
        report = meteogramPath is not null
            ? InsertMeteogramImage(report)
            : report.Replace("<!--meteogram-->", "", StringComparison.Ordinal);
        IReadOnlyDictionary<string, string>? inlineImages = meteogramPath is not null
            ? new Dictionary<string, string> { ["meteogramAbbrev"] = meteogramPath }
            : null;

        var emailer = new SmtpSender(smtp, "WxReport");
        var sent = await emailer.SendAsync(
            recipient.Email, subject, plainFallback,
            htmlBody: report, inlineImages: inlineImages,
            toName: recipient.Name, ct: ct);

        if (!sent) return;

        Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): startup report sent.");

        // WX-86: stamp and persist SentAtUtc independently of the RecipientState
        // save below, so a RecipientState failure doesn't leave a successfully
        // delivered startup report audited as SentAtUtc = null.
        committedSend.SentAtUtc = DateTime.UtcNow;
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): failed to persist CommittedSend.SentAtUtc Id={committedSend.Id} after successful startup-report send.", ex);
        }

        // Update state so the MinGap check prevents an immediate duplicate
        // from the first normal cycle.
        var state = await ctx.RecipientStates
            .FirstOrDefaultAsync(r => r.RecipientId == recipient.Id, ct);

        if (state is null)
        {
            state = new RecipientState { RecipientId = recipient.Id! };
            ctx.RecipientStates.Add(state);
        }

        state.LastUnscheduledSentUtc = DateTime.UtcNow;
        var startupHash = InputIdentity.From(snapshot).Serialize();
        state.LastClaudeInputHash = startupHash;
        state.LastSentInputHash = startupHash; // startup actually delivered a report
        state.LastMetarIcao = snapshot.StationIcao;

        try { await ctx.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): failed to save state after startup report.", ex); }
    }

    // ── cycle ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes one full report cycle: resolves each recipient's location, builds
    /// a weather snapshot, evaluates send conditions, generates a Claude report,
    /// and sends it by email.  Recipients that fail validation or resolution are
    /// skipped for this cycle only.
    /// </summary>
    /// <param name="ct">Cancellation token propagated to database and delay operations.</param>
    /// <sideeffects>
    /// Reads and writes <see cref="RecipientState"/> rows in the database.
    /// Sends email via SMTP for each qualifying recipient.
    /// Makes HTTP calls to the Claude API and address geocoding / airport lookup APIs (via <see cref="RecipientResolver"/>).
    /// Writes log entries for each recipient decision.
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

            if (cfg.Recipients.Count == 0)
            {
                Logger.Debug("No recipients configured.");
                return;
            }

            var duplicateIds = cfg.Recipients
                .Where(r => r.Id is not null)
                .GroupBy(r => r.Id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key!)
                .ToHashSet();

            foreach (var id in duplicateIds)
                Logger.Error($"Duplicate recipient Id '{id}' — all entries with this Id will be skipped.");

            var reconciler = new ForecastReconciler(
                new ClaudeClient(_claudeHttpClient, claude_cfg.ApiKey, claude_cfg.Model, _persona.Text));
            var emailer = new SmtpSender(smtp, "WxReport");
            var resolver = new RecipientResolver(_dbOptions, _httpClient, _config["What3Words:ApiKey"]);
            var now = DateTime.UtcNow;
            var reportsSent = 0;

            foreach (var recipient in cfg.Recipients)
            {
                if (string.IsNullOrWhiteSpace(recipient.Email)) continue;
                if (string.IsNullOrWhiteSpace(recipient.Id))
                {
                    Logger.Warn($"{recipient.Email} ({recipient.Name}): no Id configured — skipping. Add a unique Id via WxManager → Recipients.");
                    continue;
                }
                if (duplicateIds.Contains(recipient.Id)) continue;

                // Ensure the recipient's address has been resolved to station ICAOs.
                if (!await resolver.EnsureResolvedAsync(recipient)) continue;

                var state = await ctx.RecipientStates
                    .FirstOrDefaultAsync(r => r.RecipientId == recipient.Id, ct);

                if (state is null)
                {
                    state = new RecipientState { RecipientId = recipient.Id! };
                    ctx.RecipientStates.Add(state);
                }

                // Build a snapshot specific to this recipient's nearest stations.
                var preferredIcaos = ParseIcaoList(recipient.MetarIcao);
                var localityName = recipient.LocalityName ?? (preferredIcaos.Count > 0 ? preferredIcaos[0] : recipient.Email);
                var snapshot = await WxInterpreter.GetSnapshotAsync(
                    preferredIcaos, recipient.TafIcao == "NONE" ? null : recipient.TafIcao, localityName, _dbOptions,
                    homeLat: recipient.Latitude, homeLon: recipient.Longitude,
                    precipThresholdMmHr: cfg.PrecipRateThresholdMmHr,
                    ct: ct);

                if (snapshot is null)
                {
                    Logger.Warn($"{recipient.Id} {recipient.Email} ({recipient.Name}): no METAR, TAF, or GFS data available — skipping.");
                    continue;
                }

                if (!snapshot.ObservationAvailable)
                {
                    Logger.Warn($"{recipient.Id} {recipient.Email} ({recipient.Name}): preferred station(s) [{string.Join(", ", preferredIcaos)}] had no data and no station within 30 mi reported in the last 3 hours — sending forecast-only report.");
                }
                else if (preferredIcaos.Count > 0 && !preferredIcaos.Contains(snapshot.StationIcao))
                {
                    var distStr = snapshot.ObservationDistanceKm is double km
                        ? $" ({km * 0.621371:F0} mi away)"
                        : "";
                    Logger.Warn($"{recipient.Id} {recipient.Email} ({recipient.Name}): preferred station(s) [{string.Join(", ", preferredIcaos)}] had no data — fell back to {snapshot.StationIcao}{distStr}.");
                }

                var useC = recipient.Units.Temperature.Equals("C", StringComparison.OrdinalIgnoreCase);
                if (snapshot.GfsForecast is { } gfs)
                    Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): GFS run {gfs.ModelRunUtc:yyyy-MM-dd HH}Z — {gfs.Days.Count} day(s); " +
                        string.Join(", ", gfs.Days.Select(d => useC
                            ? $"{d.Date:MM/dd} {d.HighTempC:F0}°/{d.LowTempC:F0}°C"
                            : $"{d.Date:MM/dd} {d.HighTempF:F0}°/{d.LowTempF:F0}°F")));
                else
                    Logger.Warn($"{recipient.Id} {recipient.Email} ({recipient.Name}): no GFS forecast available.");

                // WX-80: the cheap pre-filter compares this cycle's raw input
                // identity (observation time, TAF issuance, GFS run) against the
                // identity at the last Claude call.  Significance is Claude's job,
                // not a C# fingerprint's — this only avoids paying tokens to ask
                // Claude about evidence it has already seen.
                var inputIdentity = InputIdentity.From(snapshot);
                var inputHash = inputIdentity.Serialize();

                // WX-108: which inputs are newer than at the last DELIVERED report
                // (distinct from the last Claude call). Drives the anti-reversal
                // context handed to Claude and the severe-flag hysteresis backstop.
                var changedSinceLastSend = inputIdentity.ChangedSourcesSince(state.LastSentInputHash);
                bool freshTafSinceLastSend = changedSinceLastSend.Contains(TriggerSource.Taf);
                bool freshGuidanceSinceLastSend =
                    freshTafSinceLastSend || changedSinceLastSend.Contains(TriggerSource.Gfs);

                var (shouldSend, reason, severity, allowSkip) = ShouldSend(recipient, state, inputIdentity, cfg, now);

                if (!shouldSend)
                {
                    if (reason == "prefilter-skip")
                    {
                        _preFilterSkips.Add(1);
                        Logger.Debug($"{recipient.Id} {recipient.Email} ({recipient.Name}): no input changed since last Claude call — pre-filter skip.");
                    }
                    continue;
                }

                // Label the trigger for telemetry: scheduled/first by reason, an
                // arrival by which input(s) advanced since the last Claude call.
                var triggerType = reason == "change"
                    ? ArrivalLabel(inputIdentity.ChangedSourcesSince(state.LastClaudeInputHash))
                    : reason;
                _triggers.Add(1, new KeyValuePair<string, object?>("trigger.type", triggerType));

                Logger.Info(reason == "change"
                    ? $"{recipient.Id} {recipient.Email} ({recipient.Name}): {triggerType} arrival — invoking Claude invalidation gate."
                    : $"{recipient.Id} {recipient.Email} ({recipient.Name}): generating {reason} report.");

                // Detect station switch: if the METAR source changed from what was used in
                // the last report, pass the previous ICAO to Claude so it can note the change.
                // Skip when the current snapshot has no observation — the "empty" StationIcao
                // would otherwise look like a spurious station switch.
                var previousMetarIcao = snapshot.ObservationAvailable
                    && state.LastMetarIcao is not null
                    && state.LastMetarIcao != snapshot.StationIcao
                        ? state.LastMetarIcao
                        : null;
                if (previousMetarIcao is not null)
                    Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): METAR station changed {previousMetarIcao} → {snapshot.StationIcao} — noting in report.");

                var language = recipient.Language ?? cfg.DefaultLanguage;
                var scheduledHours = ParseHourList(recipient.ScheduledSendHours ?? cfg.DefaultScheduledSendHours);
                var scheduledHour = scheduledHours.Count > 0 ? scheduledHours[0] : 7;
                var tz = ResolveTimezone(recipient.Timezone);

                // WX-78: write the provisional CommittedSend before invoking Claude.
                // Anchored to a per-recipient provisional ForecastSnapshot built
                // from the recipient's lat/lon via GfsSnapshotBuilder (WX-77).
                // Persisted now so that a Claude failure still leaves an audit row.
                // Anchor key falls back to the TAF station when the METAR station
                // is empty; GeneratedAtUtc is per-insert (not the cycle-scoped
                // `now`) so two recipients sharing a station don't collide on the
                // unique index (StationIcao, GeneratedAtUtc) — WX-79 CR findings.
                var snapshotKey = !string.IsNullOrWhiteSpace(snapshot.StationIcao)
                    ? snapshot.StationIcao
                    : (snapshot.TafStationIcao ?? "");
                var anchorSnapshot = await BuildProvisionalForecastSnapshotAsync(
                    snapshotKey, recipient.Latitude, recipient.Longitude, DateTime.UtcNow, ct);
                ctx.ForecastSnapshots.Add(anchorSnapshot);
                var committedSend = new CommittedSend
                {
                    ForecastSnapshot = anchorSnapshot,
                    RecipientId = recipient.Id!,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                ctx.CommittedSends.Add(committedSend);
                await ctx.SaveChangesAsync(ct);
                Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): wrote provisional CommittedSend Id={committedSend.Id}.");

                // Prior-snapshot lookup is recipient-keyed via CommittedSends so
                // two recipients sharing a station don't cross-contaminate each
                // other's reconciliation context.  SentAtUtc.HasValue restricts
                // to snapshots we actually delivered — Claude-failed audit rows
                // don't count as priors (WX-79 CR finding).
                var priorSnapshot = await ctx.CommittedSends
                    .Where(cs => cs.RecipientId == recipient.Id && cs.SentAtUtc.HasValue)
                    .Select(cs => cs.ForecastSnapshot)
                    .Where(s => s.Id != anchorSnapshot.Id)
                    .OrderByDescending(s => s.GeneratedAtUtc)
                    .FirstOrDefaultAsync(ct);

                var provisionalBody = ForecastSnapshotBody.Deserialize(anchorSnapshot.Body);

                // WX-114 deterministic significance gate (cost pre-filter, unscheduled
                // cycles only). When the deterministic forecast has not changed materially
                // since the last sent report, skip the Claude call entirely — this is where
                // the token savings come from. Conservative: suppresses only when no
                // criterion trips, and any evaluation error falls through to calling Claude.
                // Claude still owns the send/skip judgment whenever it is invoked.
                var gateMode = cfg.SignificanceGate.Mode;
                if (gateMode != SignificanceGateMode.Off && allowSkip && priorSnapshot is not null)
                {
                    SignificanceResult? gate = null;
                    try
                    {
                        var priorBodyForGate = ForecastSnapshotBody.Deserialize(priorSnapshot.Body);
                        gate = SignificanceGate.Evaluate(priorBodyForGate, provisionalBody, cfg.SignificanceGate, now, tz, freshTafSinceLastSend);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): WX-114 significance gate evaluation failed — proceeding to Claude.", ex);
                    }

                    if (gate is { Significant: false })
                    {
                        bool enforce = gateMode == SignificanceGateMode.Enforce;
                        _significanceGateSkips.Add(1, new KeyValuePair<string, object?>("mode", enforce ? "enforce" : "shadow"));
                        if (enforce)
                        {
                            Logger.Debug($"{recipient.Id} {recipient.Email} ({recipient.Name}): WX-114 significance gate suppressed {triggerType} cycle — no material forecast change since last sent report; Claude not called. Provisional CommittedSend Id={committedSend.Id} left unsent.");
                            await PersistUnsentCycleAsync(ctx, recipient, committedSend, state, inputHash, "WX-114 deterministic significance gate: no material change since last sent report; Claude not called.", ct);
                            continue;
                        }
                        // Shadow: log what would have been suppressed, then fall through to Claude.
                        Logger.Debug($"{recipient.Id} {recipient.Email} ({recipient.Name}): WX-114 significance gate (shadow) WOULD suppress {triggerType} cycle — no material forecast change since last sent report; calling Claude anyway.");
                    }
                    else if (gate is { Significant: true } passed)
                    {
                        Logger.Debug($"{recipient.Id} {recipient.Email} ({recipient.Name}): WX-114 significance gate passed ({gateMode}, {triggerType}) — fired: {string.Join(", ", passed.FiredCriteria)}.");
                    }
                }

                var claudeSw = Stopwatch.StartNew();
                var reconcileResult = await reconciler.ReconcileAsync(
                    snapshot, provisionalBody,
                    snapshot.GfsForecast?.ModelRunUtc,
                    snapshot.TafIssuanceUtc, snapshot.TafValidToUtc,
                    priorSnapshot,
                    language,
                    // WX-128 additive transition: the structured report carries the
                    // recipient's own language only; WX-130's per-locality loop
                    // widens this to the locality's distinct language set.
                    narrativeLanguages: new[] { LanguageHelper.ToIetfTag(language) },
                    recipient.Name, tz,
                    isFirstReport: reason == "first",
                    scheduledHour: scheduledHour,
                    units: recipient.Units,
                    changeSeverity: severity,
                    previousMetarIcao: previousMetarIcao,
                    allowSkip: allowSkip,
                    changedSinceLastSend: changedSinceLastSend,
                    ct: ct);
                _claudeDuration.Record(claudeSw.Elapsed.TotalSeconds);
                _claudeCalls.Add(1);

                // WX-80 invalidation gate: Claude judged the arrival not news.
                // Record the input hash so the next identical cycle pre-filter-skips,
                // persist the reasoning trace for diagnosability, but do NOT send and
                // do NOT advance last-sent or the committed anchor — the provisional
                // keeps SentAtUtc = null, so it never becomes a prior snapshot.
                if (reconcileResult is ReconcileResult.NotNews notNews)
                {
                    _claudeNotNews.Add(1);
                    _claudeInputTokens.Add(notNews.Tokens.InputTokens);
                    _claudeOutputTokens.Add(notNews.Tokens.OutputTokens);
                    _claudeCacheReadTokens.Add(notNews.Tokens.CacheReadInputTokens);
                    _claudeCacheCreationTokens.Add(notNews.Tokens.CacheCreationInputTokens);
                    LogClaudeTokens(recipient, notNews.Tokens, $"{triggerType}/not-news");

                    await PersistUnsentCycleAsync(ctx, recipient, committedSend, state, inputHash, notNews.ReasoningTrace, ct);
                    Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): Claude judged the {triggerType} arrival not news — no send. Provisional CommittedSend Id={committedSend.Id} left unsent.");
                    continue;
                }

                if (reconcileResult is not ReconcileResult.Success success)
                {
                    _claudeMalformedOutput.Add(1);
                    var failureReason = ((ReconcileResult.Failure)reconcileResult).Reason;
                    Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): Reconciliation failed: {failureReason}.  Provisional CommittedSend Id={committedSend.Id} left in place.");
                    continue;
                }

                _claudeToolUseSuccess.Add(1);
                _claudeInputTokens.Add(success.Tokens.InputTokens);
                _claudeOutputTokens.Add(success.Tokens.OutputTokens);
                _claudeCacheReadTokens.Add(success.Tokens.CacheReadInputTokens);
                _claudeCacheCreationTokens.Add(success.Tokens.CacheCreationInputTokens);
                LogClaudeTokens(recipient, success.Tokens, $"{triggerType}/reconciled");

                // WX-108 deterministic backstop (unscheduled cycles only). Two
                // suppressions, both about idempotence/stability rather than weather
                // significance — Claude still owns the "is it news?" judgment via the
                // gate above; this only catches what stochastic re-derivation slips past:
                //   1. Redundant re-send — the reconciled snapshot says materially what
                //      the last sent report already told this recipient.
                //   2. Severe-flag hysteresis — the only material change from the last
                //      sent snapshot is a severeFlag flip, on an observation-only advance
                //      with no newer GFS run or TAF to support it (the 06-02 348→363 case).
                // On suppression: no send, no re-anchor (provisional keeps SentAtUtc null),
                // advance LastClaudeInputHash so an identical next cycle pre-filter-skips.
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
                        await PersistUnsentCycleAsync(ctx, recipient, committedSend, state, inputHash, success.ReasoningTrace, ct);
                        Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): WX-108 suppressed {triggerType} send — {why}. Provisional CommittedSend Id={committedSend.Id} left unsent.");
                        continue;
                    }
                }

                // WX-79: persist the reconciled snapshot row and re-anchor the
                // CommittedSend.  EmailBody is the pre-meteogram wrapped HTML
                // (matches the CommittedSend.EmailBody = Claude artifact
                // convention); ReasoningTrace is the audit log Claude produced.
                var reconciledSnapshot = new ForecastSnapshot
                {
                    StationIcao = snapshot.StationIcao,
                    GeneratedAtUtc = DateTime.UtcNow,
                    SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
                    Body = success.FinalSnapshot.Serialize(),
                };
                ctx.ForecastSnapshots.Add(reconciledSnapshot);
                committedSend.ForecastSnapshot = reconciledSnapshot;
                var report = WrapAsEmailHtml(success.EmailBody, language, snapshot, tz);
                committedSend.EmailBody = report;
                committedSend.ReasoningTrace = success.ReasoningTrace;
                // WX-128: persist the unit-neutral structured report alongside the
                // email body. Unread in the additive transition; WX-129 consumes it.
                committedSend.StructuredReport = success.StructuredReport.Serialize();
                await ctx.SaveChangesAsync(ct);
                Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): reconciled CommittedSend Id={committedSend.Id} → ForecastSnapshot Id={reconciledSnapshot.Id}.");

                var subject = BuildSubject(snapshot, language, tz, severity, recipientName: recipient.Name);
                var plainFallback = SnapshotDescriber.Describe(snapshot, tz, recipient.Units);

                var plotsDir2 = new WxPaths(_config["InstallRoot"]).PlotsDir;
                var meteogramPath = FindMeteogramAbbrevPath(preferredIcaos.Count > 0 ? preferredIcaos[0] : "", recipient.Units.Temperature, recipient.Timezone, plotsDir2);
                report = meteogramPath is not null
                    ? InsertMeteogramImage(report)
                    : report.Replace("<!--meteogram-->", "", StringComparison.Ordinal);
                IReadOnlyDictionary<string, string>? inlineImages = meteogramPath is not null
                    ? new Dictionary<string, string> { ["meteogramAbbrev"] = meteogramPath }
                    : null;

                var sent = await emailer.SendAsync(
                    recipient.Email, subject, plainFallback,
                    htmlBody: report, inlineImages: inlineImages,
                    toName: recipient.Name, ct: ct);

                if (!sent) { _sendFailures.Add(1); continue; }

                _reportsSent.Add(1);
                Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): report sent.");

                // WX-78: stamp and persist the actual-sent time on the CommittedSend row
                // independently of the RecipientState save below.  A failure in the
                // RecipientState save would otherwise leave a successfully delivered
                // message audited as SentAtUtc = null, violating the lifecycle invariant
                // that null means "didn't actually send."
                committedSend.SentAtUtc = DateTime.UtcNow;
                try
                {
                    await ctx.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): failed to persist CommittedSend.SentAtUtc Id={committedSend.Id} after successful email send.", ex);
                }

                if (reason is "scheduled" or "first")
                    state.LastScheduledSentUtc = now;
                else
                    state.LastUnscheduledSentUtc = now;

                // Record the evidence identity behind this send so the next
                // unchanged cycle pre-filter-skips. Set on every actual send,
                // even observationless ones (TAF/GFS identity still advances).
                // LastSentInputHash advances only here (an actual delivery), unlike
                // LastClaudeInputHash which also advances on not-news / WX-108
                // suppression — so "changed since last send" stays accurate.
                state.LastClaudeInputHash = inputHash;
                state.LastSentInputHash = inputHash;

                // Only capture the station when we have real observation data;
                // otherwise leave the last-known value so station-switch detection
                // resumes correctly once observations return.
                if (snapshot.ObservationAvailable)
                    state.LastMetarIcao = snapshot.StationIcao;

                try
                {
                    await ctx.SaveChangesAsync(ct);
                    reportsSent++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): failed to save recipient state.", ex);
                }
            }

            Logger.Info(reportsSent > 0
                ? $"Report cycle complete. {reportsSent} report(s) sent."
                : "Report cycle complete. No reports due.");

            _reportCycles.Add(1);

        } // try
        finally
        {
            _cycleDuration.Record(cycleSw.Elapsed.TotalSeconds);
            WriteHeartbeat(cfg.HeartbeatFile);
        }
    }

    // ── persistence helpers ───────────────────────────────────────────────────

    /// <summary>WX-114: per-call token-usage DEBUG line — surfaces each Claude reconciliation's input/output/cache token split in the text log (the OTel counters carry the same totals for the dashboards, but not per call).</summary>
    private static void LogClaudeTokens(RecipientConfig recipient, TokenUsage tokens, string context) =>
        Logger.Debug($"{recipient.Id} {recipient.Email} ({recipient.Name}): Claude reconciliation tokens [{context}] — in={tokens.InputTokens} out={tokens.OutputTokens} cache-read={tokens.CacheReadInputTokens} cache-write={tokens.CacheCreationInputTokens}.");

    /// <summary>
    /// Persists the "called Claude but not sending this cycle" state shared by the
    /// WX-80 not-news gate and the WX-108 suppression backstop: records the input
    /// hash so an identical next cycle pre-filter-skips, keeps the reasoning trace
    /// on the provisional for diagnosability, and leaves the committed anchor and
    /// last-sent state untouched (the provisional keeps <c>SentAtUtc = null</c>, so
    /// it never becomes a prior snapshot). Persistence errors are logged, not
    /// thrown — an unsent cycle must not crash the worker.
    /// </summary>
    private static async Task PersistUnsentCycleAsync(
        WeatherDataContext ctx, RecipientConfig recipient, CommittedSend committedSend,
        RecipientState state, string inputHash, string reasoningTrace, CancellationToken ct)
    {
        state.LastClaudeInputHash = inputHash;
        committedSend.ReasoningTrace = reasoningTrace;
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): failed to persist unsent-cycle state for CommittedSend Id={committedSend.Id}.", ex);
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
    /// <param name="generatedAtUtc">UTC time stamp for the snapshot's unique-key column.</param>
    /// <param name="ct">Cancellation token propagated to the GFS hourly-forecast query.</param>
    /// <returns>An unattached <see cref="ForecastSnapshot"/> ready to be added to the <see cref="WeatherDataContext"/>.</returns>
    private async Task<ForecastSnapshot> BuildProvisionalForecastSnapshotAsync(
        string stationIcao, double? lat, double? lon, DateTime generatedAtUtc, CancellationToken ct)
    {
        GfsHourlyForecast? hourly = null;
        if (lat.HasValue && lon.HasValue)
        {
            hourly = await GfsInterpreter.GetHourlyForecastAsync(lat.Value, lon.Value, _dbOptions, ct);
        }

        var body = hourly is not null
            ? GfsSnapshotBuilder.Build(hourly)
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
    /// Wraps the inner HTML body produced by Claude in the standard WxReport
    /// email envelope: <c>&lt;!DOCTYPE html&gt;</c>, language-tagged
    /// <c>&lt;html&gt;</c>, viewport meta, and the dark-blue footer line
    /// carrying observation/GFS/version metadata.  The result is the
    /// pre-meteogram HTML stored on <see cref="CommittedSend.EmailBody"/>;
    /// meteogram replacement happens immediately after, before SMTP send.
    /// </summary>
    /// <param name="innerBodyHtml">Inner content of the <c>&lt;body&gt;</c> tag returned by Claude (no doctype, no html/body wrappers, no markdown).</param>
    /// <param name="language">Natural-language name of the recipient's language; used to derive the <c>lang</c> attribute via <see cref="LanguageHelper.ToIetfTag"/>.</param>
    /// <param name="snapshot">Weather snapshot supplying station + observation time + GFS run for the footer line.</param>
    /// <param name="tz">Recipient's timezone (presently unused — footer timestamps are UTC by Paul's request — but reserved for future per-recipient footer localisation).</param>
    /// <returns>The fully wrapped HTML ready for meteogram replacement and SMTP delivery.</returns>
    private static string WrapAsEmailHtml(string innerBodyHtml, string language, WeatherSnapshot snapshot, TimeZoneInfo tz)
    {
        _ = tz;
        var langCode = LanguageHelper.ToIetfTag(language);
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
    /// Determines whether to invoke Claude for a recipient right now, and why.
    /// Triggers are evaluated in priority order:
    /// <list type="number">
    ///   <item><b>first</b> — recipient has never received any report. Always sends.</item>
    ///   <item><b>scheduled</b> — the daily scheduled hour has arrived in the
    ///   recipient's timezone and no scheduled report has been sent in that slot.
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
    /// <param name="recipient">Recipient config providing timezone and scheduled-send-hour.</param>
    /// <param name="state">Persisted state: last send times and the last-Claude-call input hash.</param>
    /// <param name="inputIdentity">This cycle's raw input identity (observation time, TAF issuance, GFS run).</param>
    /// <param name="cfg">Report config providing the minimum inter-send gap and default scheduled hour.</param>
    /// <param name="nowUtc">The UTC clock time to use as "now" for all comparisons.</param>
    /// <returns>
    /// A tuple of (<c>send</c>, <c>reason</c>, <c>severity</c>, <c>allowSkip</c>).
    /// <c>reason</c> is <c>"first"</c>, <c>"scheduled"</c>, or <c>"change"</c> when
    /// sending; <c>"gap"</c> (rate-limited) or <c>"prefilter-skip"</c> (no input
    /// advanced) when not. <c>allowSkip</c> is <see langword="true"/> only for the
    /// <c>"change"</c> trigger, enabling Claude's "not news" gate; scheduled and
    /// first sends are guaranteed and never skippable.
    /// </returns>
    internal static (bool send, string reason, ChangeSeverity severity, bool allowSkip) ShouldSend(
        RecipientConfig recipient,
        RecipientState state,
        InputIdentity inputIdentity,
        ReportConfig cfg,
        DateTime nowUtc)
    {
        // Brand-new recipient — send an introductory report on the first cycle.
        if (!state.LastScheduledSentUtc.HasValue && !state.LastUnscheduledSentUtc.HasValue)
            return (true, "first", ChangeSeverity.None, false);

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
            return (false, "gap", ChangeSeverity.None, false);

        // ── Scheduled send (always sends; bypasses the pre-filter) ────────────

        var tz = ResolveTimezone(recipient.Timezone);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var scheduledHours = ParseHourList(recipient.ScheduledSendHours ?? cfg.DefaultScheduledSendHours);

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
                return (true, "scheduled", ChangeSeverity.None, false);
        }

        // ── Arrival pre-filter (WX-80) ────────────────────────────────────────

        // Cheap C# gate: has any input advanced since the last Claude call? If
        // not, skip the call entirely. If so, route to the Claude invalidation
        // gate (allowSkip) — Claude decides whether the advance is news worth an
        // unscheduled email. This replaces the deleted observation-to-observation
        // fingerprint, which decided significance in C# and misfired on KDWH.
        var changed = inputIdentity.ChangedSourcesSince(state.LastClaudeInputHash);
        if (changed.Count == 0)
            return (false, "prefilter-skip", ChangeSeverity.None, false);

        return (true, "change", ChangeSeverity.Update, true);
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
    /// Builds a localised email subject line for a weather report or alert.
    /// The subject includes the recipient's first name, the locality name, and the local observation time.
    /// When <paramref name="severity"/> is <see cref="ChangeSeverity.Alert"/> or
    /// <see cref="ChangeSeverity.Update"/>, the subject uses the corresponding label.
    /// Supported languages with translated subjects: Spanish, French; all others default to English.
    /// </summary>
    /// <param name="snap">Snapshot providing the station ICAO, locality name, and observation time.</param>
    /// <param name="language">Report language name (e.g. <c>"Spanish"</c>, <c>"French"</c>, <c>"English"</c>).</param>
    /// <param name="tz">Timezone used to convert the UTC observation time for display.</param>
    /// <param name="severity">
    /// Severity of the change that triggered this send.
    /// <see cref="ChangeSeverity.Alert"/> uses an alert label (e.g. <c>"Weather alert"</c>);
    /// <see cref="ChangeSeverity.Update"/> uses an update label (e.g. <c>"Weather update"</c>);
    /// all other values use the standard report label (e.g. <c>"Weather report"</c>).
    /// </param>
    /// <param name="recipientName">Display name of the recipient, included in the subject (e.g. <c>"Paul"</c>).</param>
    /// <returns>A localised subject string, e.g. <c>"Weather report for Paul — The Woodlands (7:05 AM)"</c>,
    /// <c>"Weather update for Paul — The Woodlands (7:05 AM)"</c>,
    /// or <c>"Weather alert for Paul — The Woodlands (7:05 AM)"</c>.</returns>
    private static string BuildSubject(WeatherSnapshot snap, string language, TimeZoneInfo tz,
        ChangeSeverity severity = ChangeSeverity.None, string recipientName = "")
    {
        // Use send-time when no observation is available; ObservationTimeUtc is
        // default(DateTime) in that case, which would render as 12:00 AM.
        var subjectTimeUtc = snap.ObservationAvailable ? snap.ObservationTimeUtc : DateTime.UtcNow;
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(subjectTimeUtc, tz).ToString("h:mm tt");
        var forName = string.IsNullOrWhiteSpace(recipientName) ? "" : $" for {recipientName}";
        if (language.Equals("Spanish", StringComparison.OrdinalIgnoreCase))
        {
            var label = severity switch
            {
                ChangeSeverity.Alert => "Alerta meteorológica",
                ChangeSeverity.Update => "Actualización del tiempo",
                _ => "Reporte del tiempo",
            };
            var paraName = string.IsNullOrWhiteSpace(recipientName) ? "" : $" para {recipientName}";
            return $"{label}{paraName} — {snap.LocalityName} ({localTime})";
        }
        if (language.Equals("French", StringComparison.OrdinalIgnoreCase))
        {
            var label = severity switch
            {
                ChangeSeverity.Alert => "Alerte météo",
                ChangeSeverity.Update => "Mise à jour météo",
                _ => "Bulletin météo",
            };
            var pourName = string.IsNullOrWhiteSpace(recipientName) ? "" : $" pour {recipientName}";
            return $"{label}{pourName} — {snap.LocalityName} ({localTime})";
        }
        {
            var label = severity switch
            {
                ChangeSeverity.Alert => "Weather alert",
                ChangeSeverity.Update => "Weather update",
                _ => "Weather report",
            };
            return $"{label}{forName} — {snap.LocalityName} ({localTime})";
        }
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
            report.Recipients = dbRecipients.Select(ToConfig).ToList();

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
    /// Converts a <see cref="Recipient"/> database entity to the <see cref="RecipientConfig"/>
    /// view model used throughout the report worker.
    /// </summary>
    /// <param name="r">The database recipient entity to convert.</param>
    /// <returns>A populated <see cref="RecipientConfig"/> instance.</returns>
    private static RecipientConfig ToConfig(Recipient r) => new()
    {
        Id = r.RecipientId,
        Email = r.Email,
        Name = r.Name,
        Language = r.Language,
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