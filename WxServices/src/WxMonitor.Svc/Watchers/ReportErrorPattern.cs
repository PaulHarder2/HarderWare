using System.Text.Json;
using System.Text.RegularExpressions;

using WxServices.Logging;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// One operator-defined error pattern scanned against shipped report bodies. Loaded from
/// <c>report-error-patterns.json</c> under InstallRoot; edited on the server with no redeploy.
/// </summary>
public sealed class ReportErrorPattern
{
    /// <summary>Stable identifier for the pattern, recorded on each finding.</summary>
    public string Id { get; set; } = "";

    /// <summary>ISO 639-1 language code this pattern applies to (e.g. <c>"de"</c>), or <c>"*"</c> for all languages.</summary>
    public string Language { get; set; } = "*";

    /// <summary>.NET regular expression matched against the report's <c>EmailBody</c>.</summary>
    public string Regex { get; set; } = "";

    /// <summary>Human-readable description of what the pattern catches (becomes the finding's body).</summary>
    public string Description { get; set; } = "";

    /// <summary>Operator-defined severity label recorded on the finding (e.g. <c>"warn"</c>).</summary>
    public string Severity { get; set; } = "warn";
}

/// <summary>A <see cref="ReportErrorPattern"/> with its regex compiled ready to match.</summary>
public sealed record CompiledPattern(ReportErrorPattern Meta, Regex Regex);

/// <summary>Loads and compiles the report-error patterns from their JSON file.</summary>
public static class ReportErrorPatternLoader
{
    // Operator-authored regexes run against report bodies; a catastrophic-backtracking pattern must
    // not wedge the whole monitor cycle, so every match is bounded by this timeout.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads and compiles patterns from <paramref name="filePath"/>. Returns an empty list if the
    /// file is absent or unreadable (the watcher is then inactive). Entries with a blank or invalid
    /// regex are skipped with a WARN, so one bad pattern never disables the rest; duplicate ids are
    /// dropped (first wins) with a WARN so the findings record stays unambiguous.
    /// </summary>
    public static IReadOnlyList<CompiledPattern> Load(string filePath)
    {
        if (!File.Exists(filePath))
            return [];

        List<ReportErrorPattern>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<ReportErrorPattern>>(File.ReadAllText(filePath), JsonOpts);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read report-error patterns from '{filePath}': {ex.Message}");
            return [];
        }

        if (raw is null)
            return [];

        var compiled = new List<CompiledPattern>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in raw)
        {
            if (string.IsNullOrWhiteSpace(p.Regex))
                continue;

            if (!seenIds.Add(p.Id))
            {
                Logger.Warn($"Report-error pattern id '{p.Id}' is duplicated, skipping the later entry.");
                continue;
            }

            try
            {
                // Interpreted (not Compiled): re-created each cycle, so the per-use JIT cost of
                // Compiled would outweigh its match-time gain on short bodies. Timeout-bounded (above).
                compiled.Add(new CompiledPattern(p, new Regex(p.Regex, RegexOptions.CultureInvariant, MatchTimeout)));
            }
            catch (ArgumentException ex)
            {
                Logger.Warn($"Report-error pattern '{p.Id}' has an invalid regex, skipping: {ex.Message}");
            }
        }

        return compiled;
    }
}