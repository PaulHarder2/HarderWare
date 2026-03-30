using System.Text.Json;
using WxServices.Logging;

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

    /// <summary>
    /// Reads the monitor state from <c>wxmonitor-state.json</c>.
    /// Returns a fresh empty state if the file does not exist or cannot be parsed.
    /// </summary>
    /// <returns>The deserialized <see cref="MonitorState"/>, or a new instance if the file is absent or corrupt.</returns>
    /// <sideeffects>Reads a file from disk. Writes a WARN log entry if the file exists but cannot be parsed.</sideeffects>
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

    /// <summary>
    /// Serializes <paramref name="state"/> to <c>wxmonitor-state.json</c>.
    /// Logs an error if the write fails but does not throw.
    /// </summary>
    /// <param name="state">The monitor state to persist.</param>
    /// <sideeffects>Creates or overwrites <c>wxmonitor-state.json</c> alongside the executable. Writes an error log entry on failure.</sideeffects>
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
