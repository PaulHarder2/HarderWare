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
    private readonly PersonaPrefix _persona;

    private readonly Meter _meter = new("WxReport.Svc", "1.0.0");
    private readonly Counter<long> _reportCycles;
    private readonly Counter<long> _reportsSent;
    private readonly Counter<long> _sendFailures;
    private readonly Counter<long> _claudeCalls;
    private readonly Histogram<double> _cycleDuration;
    private readonly Histogram<double> _claudeDuration;

    /// <summary>Initializes a new instance of <see cref="ReportWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to load the <c>Report</c> config section each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> to read/write recipient state.</param>
    /// <param name="httpClientFactory">Factory used to obtain the named <c>WxReport</c> HTTP client for Claude and geocoding calls.</param>
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
        _persona = persona;
        _reportCycles = _meter.CreateCounter<long>("wxreport.cycles.total", description: "Number of completed report cycles.");
        _reportsSent = _meter.CreateCounter<long>("wxreport.sends.total", description: "Number of reports successfully sent.");
        _sendFailures = _meter.CreateCounter<long>("wxreport.send.failures.total", description: "Number of failed email sends.");
        _claudeCalls = _meter.CreateCounter<long>("wxreport.claude.calls.total", description: "Number of Claude API calls.");
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

        var recipient = cfg.Recipients.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(r.Email) && !string.IsNullOrWhiteSpace(r.Id));

        if (recipient is null)
        {
            Logger.Debug("No valid recipient found for startup report.");
            return;
        }

        var resolver = new RecipientResolver(_dbOptions, _httpClient);
        if (!await resolver.EnsureResolvedAsync(recipient)) return;

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
        var claude = new ClaudeClient(_httpClient, claude_cfg.ApiKey, claude_cfg.Model, _persona.Text);
        var report = await claude.GenerateReportAsync(
            snapshot, language, recipient.Name, tz,
            isFirstReport: false,
            scheduledHour: scheduledHour,
            units: recipient.Units,
            ct: ct);

        if (report is null)
        {
            Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): Claude returned no report for startup send.");
            return;
        }

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
        state.LastSnapshotFingerprint = SnapshotFingerprint.Compute(snapshot, cfg.SignificantChange);
        state.LastMetarIcao = snapshot.StationIcao;

        try { await ctx.SaveChangesAsync(ct); }
        catch (Exception ex) { Logger.Error("Failed to save state after startup report.", ex); }
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

            var claude = new ClaudeClient(_httpClient, claude_cfg.ApiKey, claude_cfg.Model, _persona.Text);
            var emailer = new SmtpSender(smtp, "WxReport");
            var resolver = new RecipientResolver(_dbOptions, _httpClient);
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

                var fingerprint = SnapshotFingerprint.Compute(snapshot, cfg.SignificantChange);
                var (shouldSend, reason, severity) = ShouldSend(recipient, state, fingerprint, cfg, now,
                    observationAvailable: snapshot.ObservationAvailable);

                if (shouldSend && reason == "change" && state.LastSnapshotFingerprint is not null)
                {
                    var changeDesc = SnapshotFingerprint.DescribeChanges(
                        state.LastSnapshotFingerprint, fingerprint, snapshot, cfg.SignificantChange);
                    Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): unscheduled send triggered — {changeDesc}");
                }

                if (!shouldSend) continue;

                Logger.Info($"{recipient.Id} {recipient.Email} ({recipient.Name}): generating {reason} report.");

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
                var claudeSw = Stopwatch.StartNew();
                var report = await claude.GenerateReportAsync(
                    snapshot, language, recipient.Name, tz,
                    isFirstReport: reason == "first",
                    scheduledHour: scheduledHour,
                    units: recipient.Units,
                    changeSeverity: severity,
                    previousMetarIcao: previousMetarIcao,
                    ct: ct);
                _claudeDuration.Record(claudeSw.Elapsed.TotalSeconds);
                _claudeCalls.Add(1);

                if (report is null)
                {
                    Logger.Error($"{recipient.Id} {recipient.Email} ({recipient.Name}): Claude returned no report — skipping send.");
                    continue;
                }

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

                if (reason is "scheduled" or "first")
                    state.LastScheduledSentUtc = now;
                else
                    state.LastUnscheduledSentUtc = now;

                // Only capture the fingerprint and station when we have real observation
                // data; otherwise leave the last-known values in place so change-detection
                // resumes correctly once observations return.
                if (snapshot.ObservationAvailable)
                {
                    state.LastSnapshotFingerprint = fingerprint;
                    state.LastMetarIcao = snapshot.StationIcao;
                }

                try
                {
                    await ctx.SaveChangesAsync(ct);
                    reportsSent++;
                }
                catch (Exception ex)
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

    // ── send-decision logic ───────────────────────────────────────────────────

    /// <summary>
    /// Determines whether a weather report should be sent to a recipient right now,
    /// and why.  Three send triggers are evaluated in priority order:
    /// <list type="number">
    ///   <item><b>first</b> — recipient has never received any report.</item>
    ///   <item><b>scheduled</b> — the daily scheduled hour has arrived in the
    ///   recipient's timezone and no scheduled report has been sent today.</item>
    ///   <item><b>change</b> — conditions have changed significantly since the
    ///   last send and the minimum inter-send gap has elapsed.</item>
    /// </list>
    /// </summary>
    /// <param name="recipient">Recipient config providing timezone and scheduled-send-hour.</param>
    /// <param name="state">Persisted state recording when reports were last sent and the last known fingerprint.</param>
    /// <param name="fingerprint">Current-conditions fingerprint computed by <see cref="SnapshotFingerprint.Compute"/>.</param>
    /// <param name="cfg">Report config providing the minimum inter-send gap and default scheduled hour.</param>
    /// <param name="nowUtc">The UTC clock time to use as "now" for all comparisons.</param>
    /// <returns>
    /// A tuple of (<c>send</c>, <c>reason</c>, <c>severity</c>).
    /// When <c>send</c> is <see langword="false"/>, <c>reason</c> is an empty string
    /// and <c>severity</c> is <see cref="ChangeSeverity.None"/>.
    /// When <see langword="true"/>, <c>reason</c> is <c>"first"</c>, <c>"scheduled"</c>,
    /// or <c>"change"</c>, and <c>severity</c> is the classified change severity
    /// (<see cref="ChangeSeverity.None"/> for scheduled/first sends).
    /// A <c>"change"</c> send with <see cref="ChangeSeverity.Minor"/> is suppressed
    /// — the method returns <c>send = false</c> in that case.
    /// </returns>
    private static (bool send, string reason, ChangeSeverity severity) ShouldSend(
        RecipientConfig recipient,
        RecipientState state,
        string fingerprint,
        ReportConfig cfg,
        DateTime nowUtc,
        bool observationAvailable = true)
    {
        // Brand-new recipient — send an introductory report on the first cycle.
        if (!state.LastScheduledSentUtc.HasValue && !state.LastUnscheduledSentUtc.HasValue)
            return (true, "first", ChangeSeverity.None);

        var minGap = TimeSpan.FromMinutes(cfg.MinGapMinutes);

        // Last time any report was sent to this recipient.
        var lastSentUtc = state.LastScheduledSentUtc > state.LastUnscheduledSentUtc
            ? state.LastScheduledSentUtc
            : state.LastUnscheduledSentUtc;

        // Enforce minimum gap.
        if (lastSentUtc.HasValue && (nowUtc - lastSentUtc.Value) < minGap)
            return (false, "", ChangeSeverity.None);

        // ── Scheduled send ────────────────────────────────────────────────────

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
                return (true, "scheduled", ChangeSeverity.None);
        }

        // ── Significant-change send ───────────────────────────────────────────

        // When no current observation is available, the observation portion of
        // the fingerprint is meaningless (wind/visibility/phenomena default to
        // "calm/good/none").  Suppress change-triggered sends in that case to
        // avoid false-clearing alerts, and resume on the next cycle that has
        // real data.
        if (observationAvailable
            && state.LastSnapshotFingerprint is not null
            && state.LastSnapshotFingerprint != fingerprint)
        {
            var severity = SnapshotFingerprint.ClassifyChange(state.LastSnapshotFingerprint, fingerprint);
            if (severity == ChangeSeverity.Minor)
            {
                Logger.Debug($"Fingerprint changed but severity is Minor — suppressing unscheduled send.");
                return (false, "", ChangeSeverity.Minor);
            }
            return (true, "change", severity);
        }

        return (false, "", ChangeSeverity.None);
    }

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
    /// </summary>
    private static IReadOnlyList<int> ParseHourList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(s => int.TryParse(s, out var h) ? (int?)h : null)
                  .Where(h => h is >= 0 and <= 23)
                  .Select(h => h!.Value)
                  .OrderBy(h => h)
                  .ToList();
    }

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