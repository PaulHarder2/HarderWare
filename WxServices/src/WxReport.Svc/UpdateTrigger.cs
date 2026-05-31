using WxInterp;

namespace WxReport.Svc;

/// <summary>
/// A weather-data source whose arrival (or advance) can trigger an unscheduled
/// report.  Scheduled and first sends are not arrival-driven and so are not
/// represented here — they are decided by the clock, not by input change.
/// </summary>
public enum TriggerSource
{
    /// <summary>A newer METAR observation arrived for the station.</summary>
    Metar,

    /// <summary>A newer TAF was issued for the station.</summary>
    Taf,

    /// <summary>A newer GFS model run became available.</summary>
    Gfs,
}

/// <summary>
/// Cheap, deterministic identity of the inputs a single reconciliation cycle
/// would hand to Claude: the current observation (station + observation time),
/// the active TAF (issuance time), and the GFS model run.  Two cycles whose
/// <see cref="InputIdentity"/> serialises identically present Claude with the
/// same evidence, so the second can skip the call entirely — the WX-80
/// pre-filter: <em>has any input changed since the last Claude call?</em>
///
/// <para>
/// Deliberately raw <em>identity</em>, not thresholded conditions.  Every
/// significance judgment ("is this change worth sending?") belongs to Claude's
/// invalidation gate, not to a C# fingerprint — that judgment-in-C# was exactly
/// what the deleted <c>SnapshotFingerprint</c> got wrong (it fired the
/// 2026-04-21 KDWH double-send on an observation the forecast had predicted).
/// The pre-filter's only job is to avoid paying tokens to ask Claude about
/// evidence it has already seen.
/// </para>
/// </summary>
public readonly record struct InputIdentity(string Metar, string Taf, string Gfs)
{
    private const string None = "none";

    /// <summary>Builds the identity from a recipient's current snapshot.</summary>
    /// <param name="snap">The snapshot assembled for this cycle.</param>
    /// <returns>The input identity; absent inputs are recorded as <c>"none"</c>.</returns>
    public static InputIdentity From(WeatherSnapshot snap) => new(
        Metar: snap.ObservationAvailable
            ? $"{snap.StationIcao}@{snap.ObservationTimeUtc:O}"
            : None,
        Taf: snap.TafIssuanceUtc?.ToString("O") ?? None,
        Gfs: snap.GfsForecast?.ModelRunUtc.ToString("O") ?? None);

    /// <summary>
    /// Serialises to the compact, parseable form stored in
    /// <c>RecipientState.LastClaudeInputHash</c>, e.g.
    /// <c>"M:KDWH@2026-04-21T13:53:00.0000000Z|T:2026-04-21T12:00:00.0000000Z|G:2026-04-21T06:00:00.0000000Z"</c>.
    /// Component values never contain <c>'|'</c>, so the form round-trips through
    /// <see cref="Parse"/>.
    /// </summary>
    /// <returns>The serialised identity.</returns>
    public string Serialize() => $"M:{Metar}|T:{Taf}|G:{Gfs}";

    /// <summary>
    /// Parses a serialised identity.  Returns an all-<c>"none"</c> identity when
    /// the input is null, empty, or malformed (e.g. a <c>RecipientState</c> row
    /// written before WX-80, which has no stored hash yet) — callers treat that
    /// baseline as differing from any real input, so an unknown baseline routes
    /// to Claude rather than silently skipping a send.
    /// </summary>
    /// <param name="serialized">A value previously produced by <see cref="Serialize"/>, or null.</param>
    /// <returns>The parsed identity.</returns>
    public static InputIdentity Parse(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized)) return new(None, None, None);
        string m = None, t = None, g = None;
        foreach (var part in serialized.Split('|'))
        {
            if (part.Length < 2 || part[1] != ':') continue;
            var val = part[2..];
            switch (part[0])
            {
                case 'M': m = val; break;
                case 'T': t = val; break;
                case 'G': g = val; break;
            }
        }
        return new(m, t, g);
    }

    /// <summary>
    /// Returns the sources whose identity differs from <paramref name="previousHash"/>
    /// — i.e. which inputs arrived or advanced since the last Claude call.
    /// Empty when nothing changed (the pre-filter skip case).
    /// </summary>
    /// <param name="previousHash">The serialised identity stored at the last Claude call, or null.</param>
    /// <returns>The changed sources, in METAR/TAF/GFS order.</returns>
    public IReadOnlyList<TriggerSource> ChangedSourcesSince(string? previousHash)
    {
        var prev = Parse(previousHash);
        var changed = new List<TriggerSource>(3);
        if (Metar != prev.Metar) changed.Add(TriggerSource.Metar);
        if (Taf != prev.Taf) changed.Add(TriggerSource.Taf);
        if (Gfs != prev.Gfs) changed.Add(TriggerSource.Gfs);
        return changed;
    }
}