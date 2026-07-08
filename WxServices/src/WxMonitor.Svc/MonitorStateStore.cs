using System.Text.Json;

using WxServices.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Loads and persists <see cref="MonitorState"/> across service restarts.
/// Extracted as an interface so the monitor cycle can be exercised in tests
/// against an in-memory store rather than the on-disk file.
/// </summary>
public interface IMonitorStateStore
{
    /// <summary>Reads the persisted monitor state, or a fresh empty state if none exists.</summary>
    MonitorState Load();

    /// <summary>Persists the monitor state.</summary>
    void Save(MonitorState state);
}

/// <summary>
/// Reads and writes <see cref="MonitorState"/> to a JSON file (<c>wxmonitor-state.json</c>).
/// Defaults to the executable's base directory; the directory is injectable for testing.
/// </summary>
public sealed class MonitorStateStore : IMonitorStateStore
{
    private readonly string _statePath;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Creates a store writing to <c>wxmonitor-state.json</c> in <paramref name="directory"/>
    /// (defaults to <see cref="AppContext.BaseDirectory"/> — the historical location).
    /// </summary>
    public MonitorStateStore(string? directory = null)
    {
        _statePath = Path.Combine(directory ?? AppContext.BaseDirectory, "wxmonitor-state.json");
    }

    /// <summary>
    /// Reads the monitor state from <c>wxmonitor-state.json</c>.
    /// Returns a fresh empty state if the file does not exist or cannot be parsed.
    /// </summary>
    /// <returns>The deserialized <see cref="MonitorState"/>, or a new instance if the file is absent or corrupt.</returns>
    /// <sideeffects>Reads a file from disk. Writes a WARN log entry if the file exists but cannot be parsed.</sideeffects>
    public MonitorState Load()
    {
        if (!File.Exists(_statePath))
            return new MonitorState();

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<MonitorState>(json, JsonOpts) ?? new MonitorState();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read monitor state from '{_statePath}': {ex.Message} — starting fresh.");
            return new MonitorState();
        }
    }

    /// <summary>
    /// Serializes <paramref name="state"/> to <c>wxmonitor-state.json</c>.
    /// Logs an error if the write fails but does not throw.
    /// </summary>
    /// <param name="state">The monitor state to persist.</param>
    /// <sideeffects>Creates or overwrites <c>wxmonitor-state.json</c>. Writes an error log entry on failure.</sideeffects>
    public void Save(MonitorState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, JsonOpts);
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not save monitor state to '{_statePath}': {ex.Message}");
        }
    }
}