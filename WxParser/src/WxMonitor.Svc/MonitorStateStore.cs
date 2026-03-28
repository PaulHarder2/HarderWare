using System.Text.Json;
using WxParser.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Reads and writes <see cref="MonitorState"/> to a JSON file alongside the executable.
/// </summary>
public static class MonitorStateStore
{
    private static readonly string StatePath =
        Path.Combine(AppContext.BaseDirectory, "wxmonitor-state.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = true,
        PropertyNameCaseInsensitive = true,
    };

    public static MonitorState Load()
    {
        if (!File.Exists(StatePath))
            return new MonitorState();

        try
        {
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<MonitorState>(json, JsonOpts) ?? new MonitorState();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read monitor state from '{StatePath}': {ex.Message} — starting fresh.");
            return new MonitorState();
        }
    }

    public static void Save(MonitorState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOpts);
            File.WriteAllText(StatePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not save monitor state to '{StatePath}': {ex.Message}");
        }
    }
}
