using System.Text.RegularExpressions;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxServices.Logging;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Scans shipped report bodies (<c>CommittedSend.EmailBody</c>) for operator-defined, language-scoped
/// error patterns and records new hits. Language is attributed live from the recipient's current
/// <c>Language</c> as each send is scanned, which dissolves the "CommittedSend has no language column"
/// caveat for forward monitoring. Patterns load from <c>report-error-patterns.json</c> under InstallRoot
/// (re-read each cycle → live reload). Only sends past a per-cycle watermark on <c>SentAtUtc</c> are
/// scanned; findings go to the JSONL sink (a durable record), never email — so there is no cooldown.
/// </summary>
public sealed class ReportErrorWatcher : IWatcher
{
    /// <summary>The watcher's stable id, shared with the scheduler's sink routing.</summary>
    public const string WatcherId = "report-errors";

    private const string DefaultLanguage = "en";
    private const int SnippetPad = 40;

    /// <inheritdoc/>
    public string Id => WatcherId;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Finding>> RunAsync(WatcherContext ctx, CancellationToken ct)
    {
        var patterns = ReportErrorPatternLoader.Load(
            Path.Combine(ctx.Paths.InstallRoot, "report-error-patterns.json"));
        if (patterns.Count == 0)
            return [];   // watcher is inactive until patterns are configured

        await using var db = new WeatherDataContext(ctx.DbOptions);

        // Production sends that actually shipped (exclude provisional/failed and diagnostic rows).
        var qualifying = db.CommittedSends
            .Where(c => c.SentAtUtc != null && !c.IsDiagnostic && c.EmailBody != null);

        var watermark = ctx.State.LastReportScanUtc;

        // First run since activation: baseline to the latest send time and scan no history (forward only).
        if (watermark is null)
        {
            var maxSent = await qualifying.MaxAsync(c => c.SentAtUtc, ct);
            if (maxSent is not null)
            {
                ctx.State.LastReportScanUtc = maxSent;
                ctx.MarkStateDirty();
            }
            return [];
        }

        // Watermark on SentAtUtc (not Id): Id is assigned at provisional insert, SentAtUtc at send
        // completion, so SentAtUtc is monotonic with the order sends ship — a lower-Id send that ships
        // after a higher-Id one is not skipped.
        var newSends = await qualifying
            .Where(c => c.SentAtUtc > watermark.Value)
            .OrderBy(c => c.SentAtUtc)
            .Select(c => new { c.Id, c.RecipientId, c.EmailBody, c.SentAtUtc })
            .ToListAsync(ct);

        if (newSends.Count == 0)
            return [];

        // Live roster: recipient stable id → language ISO code (null when the recipient uses the default).
        var roster = await db.Recipients
            .Select(r => new { r.RecipientId, IsoCode = r.Language != null ? r.Language.IsoCode : null })
            .ToDictionaryAsync(r => r.RecipientId, r => r.IsoCode, ct);

        var byLanguage = patterns
            .Where(p => p.Meta.Language != "*")
            .GroupBy(p => p.Meta.Language, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var wildcard = patterns.Where(p => p.Meta.Language == "*").ToList();

        var findings = new List<Finding>();
        var maxSeen = watermark.Value;

        foreach (var send in newSends)
        {
            if (send.SentAtUtc > maxSeen)
                maxSeen = send.SentAtUtc.Value;

            var body = send.EmailBody!;   // non-null filtered in the query
            var iso = (roster.TryGetValue(send.RecipientId, out var code) ? code : null) ?? DefaultLanguage;

            var applicable = byLanguage.TryGetValue(iso, out var langPatterns)
                ? wildcard.Concat(langPatterns)
                : wildcard;

            foreach (var p in applicable)
            {
                Match match;
                try
                {
                    match = p.Regex.Match(body);
                }
                catch (RegexMatchTimeoutException)
                {
                    Logger.Warn($"Report-error pattern '{p.Meta.Id}' timed out matching send {send.Id}, skipping.");
                    continue;
                }

                if (!match.Success)
                    continue;   // one finding per (send, pattern) — the (reportId, patternId) dedup

                findings.Add(new Finding(
                    Id,
                    Subject: $"report-error '{p.Meta.Id}' in send {send.Id}",
                    Body: p.Meta.Description,
                    Fields: new Dictionary<string, string>
                    {
                        ["reportId"] = send.Id.ToString(),
                        ["recipient"] = send.RecipientId,
                        ["language"] = iso,
                        ["patternId"] = p.Meta.Id,
                        ["severity"] = p.Meta.Severity,
                        ["description"] = p.Meta.Description,
                        ["snippet"] = Snippet(body, match.Index, match.Length),
                    }));
            }
        }

        if (maxSeen != watermark.Value)
        {
            ctx.State.LastReportScanUtc = maxSeen;
            ctx.MarkStateDirty();
        }

        return findings;
    }

    /// <summary>Returns a short single-line context window around a match for the findings record.</summary>
    private static string Snippet(string body, int index, int length)
    {
        var start = Math.Max(0, index - SnippetPad);
        var end = Math.Min(body.Length, index + length + SnippetPad);
        return body[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}