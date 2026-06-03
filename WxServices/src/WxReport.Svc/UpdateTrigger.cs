using WxInterp;

using WxServices.Common;

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
/// Still <em>identity</em>, not a send/no-send judgment.  Every significance
/// judgment ("is this change worth sending?") belongs to Claude's invalidation
/// gate, not to a C# fingerprint — that judgment-in-C# was exactly what the
/// deleted <c>SnapshotFingerprint</c> got wrong (it fired the 2026-04-21 KDWH
/// double-send on an observation the forecast had predicted).  The pre-filter's
/// only job is to avoid paying tokens to ask Claude about evidence it has
/// already seen.
/// </para>
///
/// <para>
/// WX-110: the METAR component is a coarse <em>material</em> signature (station +
/// public-meaningful wind / visibility / sky / ~5°F temperature bands + present-
/// weather tokens) rather than the raw observation timestamp.  Two consecutive
/// observations a reader would describe the same way collapse to the same
/// signature, so an unchanged hourly re-observation no longer churns Claude —
/// the dominant WxReport cost leak.  This is the same principle (identity, not
/// significance): it never decides whether to <em>send</em>, only whether the
/// evidence is materially what Claude already evaluated.  TAF and GFS still key
/// on issuance/run time, so genuinely new guidance always reaches the gate.
/// </para>
/// </summary>
public readonly record struct InputIdentity(string Metar, string Taf, string Gfs)
{
    private const string None = "none";

    /// <summary>Builds the identity from a recipient's current snapshot.</summary>
    /// <param name="snap">The snapshot assembled for this cycle.</param>
    /// <returns>The input identity; absent inputs are recorded as <c>"none"</c>.</returns>
    public static InputIdentity From(WeatherSnapshot snap) => new(
        Metar: snap.ObservationAvailable ? MetarMaterialSignature(snap) : None,
        Taf: snap.TafIssuanceUtc?.ToString("O") ?? None,
        Gfs: snap.GfsForecast?.ModelRunUtc.ToString("O") ?? None);

    // ── WX-110 material METAR signature ───────────────────────────────────────

    // Builds the coarse material signature for the METAR component: station plus
    // public-meaningful bands. Contains no '|' (the InputIdentity field separator),
    // so it round-trips through Serialize/Parse unchanged.
    private static string MetarMaterialSignature(WeatherSnapshot snap)
    {
        int wind = WindBand(snap.WindSpeedKt, snap.WindGustKt);
        int vis = VisibilityBand(snap.VisibilityStatuteMiles, snap.Cavok, snap.VisibilityLessThan);
        int sky = SkyBand(snap.SkyLayers);
        string temp = TemperatureBand(snap.TemperatureFahrenheit);
        string wx = WeatherSignature(snap.WeatherPhenomena);
        return $"{snap.StationIcao};W{wind};V{vis};S{sky};T{temp};P{wx}";
    }

    // Peak wind (greater of sustained and gust), binned via the shared public
    // wind scale (WxServices.Common.WindScale). Calm / unreported → 0.
    internal static int WindBand(int? speedKt, int? gustKt) =>
        WindScale.Band(Math.Max(speedKt ?? 0, gustKt ?? 0));

    // Visibility matters to the public only below ~1 statute mile; above that it is
    // largely irrelevant. Two classes: 0 = below 1 mi, 1 = 1 mi or better. CAVOK
    // and an unreported value are the unremarkable case (1) — a missing reading
    // stays stable rather than flipping the signature.
    internal static int VisibilityBand(double? visMiles, bool cavok, bool lessThan)
    {
        if (cavok || visMiles is null) return 1;
        bool below = visMiles.Value < 1.0 || (visMiles.Value <= 1.0 && lessThan);
        return below ? 0 : 1;
    }

    // Densest reported sky coverage, collapsed to four public classes:
    // 0 = clear (CLR/SKC/FEW/NSC/NCD), 1 = partly cloudy (SCT), 2 = mostly cloudy
    // (BKN), 3 = overcast (OVC) or obscured sky (VV).
    internal static int SkyBand(IReadOnlyList<SkyLayer> layers)
    {
        int band = 0;
        foreach (var layer in layers)
        {
            int rank = layer.Coverage switch
            {
                SkyCoverage.Scattered => 1,
                SkyCoverage.Broken => 2,
                SkyCoverage.Overcast or SkyCoverage.VerticalVisibility => 3,
                _ => 0,
            };
            if (rank > band) band = rank;
        }
        return band;
    }

    // Temperature in ~5°F bands, so ordinary hourly wobble does not churn Claude
    // while a genuine swing (front, rapid warming) still eventually crosses a band.
    // "na" when unreported, so a missing reading is stable. (Forecaster-chosen 5°F
    // width — revisit once the dashboard shows the resulting trigger rate.)
    internal static string TemperatureBand(double? tempF) =>
        tempF is { } f ? ((int)Math.Floor(f / 5.0)).ToString() : "na";

    // Stable, order-independent set of materially-relevant present-weather tokens:
    // precipitation types, the descriptor qualifiers (thunderstorm, freezing,
    // showers, …), obscurations, and other phenomena (squalls, funnel clouds).
    // Recent-weather (RE) groups are excluded — they describe what just ended, not
    // current state. Empty string when nothing significant is occurring.
    internal static string WeatherSignature(IReadOnlyList<SnapshotWeather> phenomena)
    {
        var tokens = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var wx in phenomena)
        {
            if (wx.IsRecent) continue;
            if (wx.Descriptor is { } d) tokens.Add(d.ToString().ToLowerInvariant());
            foreach (var p in wx.Precipitation) tokens.Add(p.ToString().ToLowerInvariant());
            if (wx.Obscuration is { } o) tokens.Add(o.ToString().ToLowerInvariant());
            if (wx.Other is { } other) tokens.Add(other.ToString().ToLowerInvariant());
        }
        return string.Join(",", tokens);
    }

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
        // Fail closed: anything other than exactly the three expected "K:V"
        // segments (M, T, G — each present once, each with a non-empty value)
        // discards the whole identity to the all-"none" baseline.  A corrupt or
        // partial stored hash then reads as differing from any real input,
        // routing the cycle to Claude rather than silently skipping a send on a
        // half-parsed identity.  Serialize always emits exactly "M:..|T:..|G:..".
        if (string.IsNullOrWhiteSpace(serialized)) return new(None, None, None);
        var parts = serialized.Split('|');
        if (parts.Length != 3) return new(None, None, None);

        string? m = null, t = null, g = null;
        foreach (var part in parts)
        {
            if (part.Length < 3 || part[1] != ':') return new(None, None, None);
            var val = part[2..];
            switch (part[0])
            {
                case 'M' when m is null: m = val; break;
                case 'T' when t is null: t = val; break;
                case 'G' when g is null: g = val; break;
                default: return new(None, None, None); // unknown or duplicate key
            }
        }
        return new(m, t, g); // all three are non-null here: 3 parts, no dupes, no unknowns
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