using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

using WxServices.Logging;

namespace WxManager;

/// <summary>
/// WX-235 — the single, app-lifetime owner of "Rerun QA" state, shared by the Translation-QA and
/// Vocabulary tabs. It writes a <see cref="QaRerunRequest"/> row when the operator presses the button and
/// polls the table on one <see cref="DispatcherTimer"/> (every <see cref="PollSeconds"/>s), raising
/// <see cref="StatusChanged"/> per language whose state moved. A long-lived singleton (created in
/// <c>App.OnStartup</c>) deliberately avoids per-tab view-models and their dispose/leak pitfalls: one
/// owner, one timer, rehydrated from the DB at startup so a run already in flight shows at once.
/// <para>
/// Judge-agnostic by design: it tracks "a regeneration" (the request row), never a specific judge — the
/// WxReport.Svc worker chooses the concrete judge. Nothing here names Gemini.
/// </para>
/// </summary>
public sealed class QaRerunCoordinator
{
    private const int PollSeconds = 10;

    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, QaRerunView> _byIso = new(System.StringComparer.Ordinal);
    private bool _pollInFlight;
    private int _requestVersion;   // bumped by RequestRerunAsync; lets a poll detect that its snapshot was overtaken
    private string? _lastPollError; // throttles poll-failure logging to once per distinct error message

    /// <summary>Raised (on the UI thread) with the ISO code of a language whose rerun state just changed.</summary>
    public event System.Action<string>? StatusChanged;

    public QaRerunCoordinator(DbContextOptions<WeatherDataContext> dbOptions)
    {
        _dbOptions = dbOptions;
        _timer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(PollSeconds) };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();   // rehydrate immediately
    }

    /// <summary>The current view for a language; <see cref="QaRerunView.None"/> if there is no row.</summary>
    public QaRerunView StatusFor(string iso) =>
        iso is not null && _byIso.TryGetValue(iso, out var v) ? v : QaRerunView.None;

    /// <summary>True while a language's regeneration is in flight (<see cref="QaRerunStatus.Running"/>).</summary>
    public bool IsInFlight(string iso) => StatusFor(iso).Status == QaRerunStatus.Running;

    /// <summary>
    /// Request a rerun for a language: writes <see cref="QaRerunStatus.Running"/> immediately (no-op if one is
    /// already in flight), updates the in-memory view, and raises <see cref="StatusChanged"/> synchronously so
    /// the button locks and the editable affordance freezes at once — no poll-cadence wait.
    /// </summary>
    public async Task RequestRerunAsync(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso) || IsInFlight(iso))
            return;

        var now = System.DateTime.UtcNow;
        await using (var ctx = new WeatherDataContext(_dbOptions))
        {
            var row = await ctx.QaRerunRequests.FirstOrDefaultAsync(r => r.IsoCode == iso);
            if (row is { Status: QaRerunStatus.Running })
                return;   // already running at the DB
            if (row is null)
            {
                row = new QaRerunRequest { IsoCode = iso };
                ctx.QaRerunRequests.Add(row);
            }
            row.Status = QaRerunStatus.Running;
            row.RequestedAtUtc = now;
            row.StartedAtUtc = null;
            row.CompletedAtUtc = null;
            row.ResultStamp = null;
            row.Error = null;
            row.RequestedBy = System.Environment.UserName;
            await ctx.SaveChangesAsync();
        }

        _byIso[iso] = new QaRerunView(QaRerunStatus.Running, null, null, null);
        _requestVersion++;   // any poll currently awaiting its DB read now holds a stale snapshot
        StatusChanged?.Invoke(iso);
    }

    private async Task PollAsync()
    {
        if (_pollInFlight)
            return;
        _pollInFlight = true;
        try
        {
            var versionBefore = _requestVersion;
            List<QaRerunRequest> rows;
            await using (var ctx = new WeatherDataContext(_dbOptions))
                rows = await ctx.QaRerunRequests.AsNoTracking().ToListAsync();

            // If an operator request mutated state during our read, our snapshot predates that optimistic
            // Running write — applying it would revert the just-locked button. Skip; the next poll re-reads.
            if (_requestVersion != versionBefore)
                return;

            var fresh = rows.ToDictionary(
                r => r.IsoCode,
                r => new QaRerunView(r.Status, r.Error, r.ResultStamp, r.CompletedAtUtc),
                System.StringComparer.Ordinal);

            // A claim (StartedAtUtc set, Status still Running) is NOT in the view, so it raises nothing —
            // the in-progress button stays put. Only a real state move (Running→Succeeded/Failed) fires.
            foreach (var kv in fresh)
                if (!_byIso.TryGetValue(kv.Key, out var old) || old != kv.Value)
                {
                    _byIso[kv.Key] = kv.Value;
                    StatusChanged?.Invoke(kv.Key);
                }

            foreach (var iso in _byIso.Keys.Where(k => !fresh.ContainsKey(k)).ToList())
            {
                _byIso.Remove(iso);
                StatusChanged?.Invoke(iso);
            }

            _lastPollError = null;   // a clean poll re-arms logging so a later recurrence is recorded again
        }
        catch (System.Exception ex)
        {
            // Never throw from the timer tick; transient DB hiccups resolve on the next poll. But a persistent
            // failure (e.g. the table missing pre-migration) would otherwise be invisible — the UI just stops
            // updating — so log it once per distinct error rather than every PollSeconds.
            if (ex.Message != _lastPollError)
            {
                _lastPollError = ex.Message;
                Logger.Error("QaRerunCoordinator poll failed; rerun status may be stale until it recovers.", ex);
            }
        }
        finally
        {
            _pollInFlight = false;
        }
    }
}

/// <summary>An immutable snapshot of a language's rerun state for the UI (StartedAtUtc deliberately omitted — the claim is invisible to the operator).</summary>
public sealed record QaRerunView(QaRerunStatus? Status, string? Error, string? ResultStamp, System.DateTime? CompletedAtUtc)
{
    /// <summary>No request on record for the language.</summary>
    public static readonly QaRerunView None = new(null, null, null, null);
}