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

    public ReportWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions,
        IHttpClientFactory httpClientFactory)
    {
        _config     = config;
        _dbOptions  = dbOptions;
        _httpClient = httpClientFactory.CreateClient("WxReport");
    }

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
            Logger.Info($"Next report check in {cfg.IntervalMinutes} minute(s).");
            try { await Task.Delay(TimeSpan.FromMinutes(cfg.IntervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { }
        }

        Logger.Info("ReportWorker stopped.");
    }

    // ── cycle ─────────────────────────────────────────────────────────────────

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
                preferredIcaos, recipient.TafIcao, localityName, _dbOptions);

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
    }

    // ── send-decision logic ───────────────────────────────────────────────────

    /// <returns>Whether a report should be sent and the reason ("scheduled" or "change").</returns>
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

    private static TimeZoneInfo ResolveTimezone(string id)
    {
        try   { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string BuildSubject(WeatherSnapshot snap, string language, TimeZoneInfo tz)
    {
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(snap.ObservationTimeUtc, tz).ToString("h:mm tt");
        if (language.Equals("Spanish", StringComparison.OrdinalIgnoreCase))
            return $"Reporte del tiempo — {snap.LocalityName} ({localTime})";
        if (language.Equals("French", StringComparison.OrdinalIgnoreCase))
            return $"Bulletin météo — {snap.LocalityName} ({localTime})";
        return $"Weather report — {snap.LocalityName} ({localTime})";
    }

    private static IReadOnlyList<string> ParseIcaoList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(s => !string.IsNullOrWhiteSpace(s))
                 .ToList();

    private ReportConfig LoadConfig()
    {
        var cfg = new ReportConfig();
        _config.GetSection("Report").Bind(cfg);
        return cfg;
    }
}
