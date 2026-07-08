using System.Text.Encodings.Web;
using System.Text.Json;

using WxServices.Logging;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Appends each finding as one JSON object per line (JSONL) to a findings file — a durable,
/// queryable record kept separate from the operational rolling log. Used by watchers whose output
/// is a record to review rather than an alert to page a human, so it does not rate-limit. Each line
/// is <c>{ timestamp, watcher, ...finding.Fields }</c>.
/// </summary>
public sealed class JsonlSink(string filePath, DateTime nowUtc) : ISink
{
    // Relaxed escaping so diacritics (e.g. Esperanto) and body snippets stay human-readable in the
    // findings file rather than being emitted as \uXXXX escapes.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc/>
    public async Task EmitAsync(Finding finding, CancellationToken ct)
    {
        var record = new Dictionary<string, string>
        {
            ["timestamp"] = nowUtc.ToString("O"),
            ["watcher"] = finding.WatcherId,
        };
        if (finding.Fields is not null)
        {
            foreach (var (key, value) in finding.Fields)
                record[key] = value;
        }

        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);   // findings must never be dropped just because the dir is absent
            var line = JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine;
            await File.AppendAllTextAsync(filePath, line, ct);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not append to findings file '{filePath}': {ex.Message}");
        }
    }
}