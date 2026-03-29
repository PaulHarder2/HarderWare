using MetarParser.Data;
using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WxInterp;
using WxParser.Logging;

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
    private readonly IConfiguration                         _config;
    private readonly DbContextOptions<WeatherDataContext>   _dbOptions;
    private readonly HttpClient                             _httpClient;

    /// <summary>Initializes a new instance of <see cref="ReportWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to load the <c>Report</c> config section each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> to read/write recipient state.</param>
    /// <param name="httpClientFactory">Factory used to obtain the named <c>WxReport</c> HTTP client for Claude and geocoding calls.</param>
    public ReportWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions,
        IHttpClientFactory httpClientFactory)
    {
        _config     = config;
        _dbOptions  = dbOptions;
        _httpClient = httpClientFactory.CreateClient("WxReport");
    }

    /// <summary>
    /// Entry point called by the .NET hosted-service infrastructure.
    /// Runs <see cref="RunCycleAsync"/> in a loop, sleeping for
    /// <c>Report:IntervalMinutes</c> between iterations, until the host requests shutdown.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    /// <sideeffects>Writes log entries on start, each cycle, and on stop.</sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("ReportWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.Error("Unhandled exception in report cycle.", ex);
            }

            var cfg = LoadConfig();
            var intervalMinutes = cfg.IntervalMinutes;
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
        var cfg = LoadConfig();

        if (string.IsNullOrWhiteSpace(cfg.Claude.ApiKey))
        {
            Logger.Warn("Report.Claude.ApiKey is not set — skipping report cycle.");
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

        var claude      = new ClaudeClient(_httpClient, cfg.Claude.ApiKey, cfg.Claude.Model);
        var emailer     = new EmailSender(cfg.Smtp);
        var resolver    = new RecipientResolver(_dbOptions, _httpClient);
        var now         = DateTime.UtcNow;
        var reportsSent = 0;

        await using var ctx = new WeatherDataContext(_dbOptions);

        foreach (var recipient in cfg.Recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient.Email)) continue;
            if (string.IsNullOrWhiteSpace(recipient.Id))
            {
                Logger.Warn($"{recipient.Email}: no Id configured — skipping. Add a unique Id to appsettings.local.json.");
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
            var localityName   = recipient.LocalityName ?? (preferredIcaos.Count > 0 ? preferredIcaos[0] : recipient.Email);
            var snapshot = await WxInterpreter.GetSnapshotAsync(
                preferredIcaos, recipient.TafIcao, localityName, _dbOptions, ct);

            if (snapshot is null)
            {
                Logger.Warn($"No METAR data for {recipient.Email} ({recipient.MetarIcao}) — skipping.");
                continue;
            }

            if (preferredIcaos.Count > 0 && !preferredIcaos.Contains(snapshot.StationIcao))
                Logger.Warn($"{recipient.Email}: preferred station(s) [{string.Join(", ", preferredIcaos)}] had no data — fell back to {snapshot.StationIcao}.");

            var fingerprint      = SnapshotFingerprint.Compute(snapshot, cfg.SignificantChange);
            var (shouldSend, reason) = ShouldSend(recipient, state, fingerprint, cfg, now);

            if (!shouldSend) continue;

            Logger.Info($"Generating {reason} report for {recipient.Email}.");

            var language      = recipient.Language ?? cfg.DefaultLanguage;
            var scheduledHour = recipient.ScheduledSendHour ?? cfg.DefaultScheduledSendHour;
            var tz            = ResolveTimezone(recipient.Timezone);
            var report        = await claude.GenerateReportAsync(
                snapshot, language, recipient.Name, tz,
                isFirstReport: reason == "first",
                scheduledHour: scheduledHour);

            if (report is null)
            {
                Logger.Error($"Claude returned no report for {recipient.Email} — skipping send.");
                continue;
            }

            var subject = BuildSubject(snapshot, language, tz);
            var sent    = await emailer.SendAsync(recipient.Email, recipient.Name, subject, report);

            if (!sent) continue;

            Logger.Info($"Report sent to {recipient.Email}.");

            if (reason is "scheduled" or "first")
                state.LastScheduledSentUtc   = now;
            else
                state.LastUnscheduledSentUtc  = now;

            state.LastSnapshotFingerprint = fingerprint;

            try
            {
                await ctx.SaveChangesAsync(ct);
                reportsSent++;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to save recipient state for {recipient.Email}.", ex);
            }
        }

        Logger.Info(reportsSent > 0
            ? $"Report cycle complete. {reportsSent} report(s) sent."
            : "Report cycle complete. No reports due.");

        WriteHeartbeat(cfg.HeartbeatFile);
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
    /// A tuple of (<c>send</c>, <c>reason</c>).  When <c>send</c> is <see langword="false"/>,
    /// <c>reason</c> is an empty string.  When <see langword="true"/>, <c>reason</c> is
    /// <c>"first"</c>, <c>"scheduled"</c>, or <c>"change"</c>.
    /// </returns>
    private static (bool send, string reason) ShouldSend(
        RecipientConfig     recipient,
        RecipientState      state,
        string              fingerprint,
        ReportConfig        cfg,
        DateTime            nowUtc)
    {
        // Brand-new recipient — send an introductory report on the first cycle.
        if (!state.LastScheduledSentUtc.HasValue && !state.LastUnscheduledSentUtc.HasValue)
            return (true, "first");

        var minGap = TimeSpan.FromMinutes(cfg.MinGapMinutes);

        // Last time any report was sent to this recipient.
        var lastSentUtc = state.LastScheduledSentUtc > state.LastUnscheduledSentUtc
            ? state.LastScheduledSentUtc
            : state.LastUnscheduledSentUtc;

        // Enforce minimum gap.
        if (lastSentUtc.HasValue && (nowUtc - lastSentUtc.Value) < minGap)
            return (false, "");

        // ── Scheduled send ────────────────────────────────────────────────────

        var tz            = ResolveTimezone(recipient.Timezone);
        var localNow      = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var scheduledHour = recipient.ScheduledSendHour ?? cfg.DefaultScheduledSendHour;

        // Has the scheduled hour arrived today and not yet been sent today?
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0), tz);

        var scheduledOnceToday = state.LastScheduledSentUtc.HasValue
            && state.LastScheduledSentUtc.Value >= todayStartUtc;

        if (localNow.Hour >= scheduledHour && !scheduledOnceToday)
            return (true, "scheduled");

        // ── Significant-change send ───────────────────────────────────────────

        if (state.LastSnapshotFingerprint is not null
            && state.LastSnapshotFingerprint != fingerprint)
        {
            return (true, "change");
        }

        return (false, "");
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
            Logger.Warn($"Timezone '{id}' was not recognised on this system — falling back to UTC. Check the Timezone setting in appsettings.local.json.");
            return TimeZoneInfo.Utc;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a localised email subject line for a weather report.
    /// The subject includes the locality name and the local observation time.
    /// Supported languages with translated subjects: Spanish, French; all others default to English.
    /// </summary>
    /// <param name="snap">Snapshot providing the station ICAO, locality name, and observation time.</param>
    /// <param name="language">Report language name (e.g. <c>"Spanish"</c>, <c>"French"</c>, <c>"English"</c>).</param>
    /// <param name="tz">Timezone used to convert the UTC observation time for display.</param>
    /// <returns>A localised subject string, e.g. <c>"Weather report — The Woodlands (7:05 AM)"</c>.</returns>
    private static string BuildSubject(WeatherSnapshot snap, string language, TimeZoneInfo tz)
    {
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(snap.ObservationTimeUtc, tz).ToString("h:mm tt");
        if (language.Equals("Spanish", StringComparison.OrdinalIgnoreCase))
            return $"Reporte del tiempo — {snap.LocalityName} ({localTime})";
        if (language.Equals("French", StringComparison.OrdinalIgnoreCase))
            return $"Bulletin météo — {snap.LocalityName} ({localTime})";
        return $"Weather report — {snap.LocalityName} ({localTime})";
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
        try   { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
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
    /// Loads and returns the current <see cref="ReportConfig"/> from the
    /// <c>Report</c> section of the application configuration.
    /// Called at the start of each cycle so that config changes take effect
    /// without restarting the service.
    /// </summary>
    /// <returns>A freshly bound <see cref="ReportConfig"/> reflecting the current appsettings.</returns>
    private ReportConfig LoadConfig()
    {
        var cfg = new ReportConfig();
        _config.GetSection("Report").Bind(cfg);
        return cfg;
    }
}
