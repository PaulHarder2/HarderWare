using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using WxServices.Common;

using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// The WX-140 station gap fill, shared by the per-cycle fetch worker and (in
/// WX-141) WxManager's recipient-save flow: finds <em>defined</em> stations
/// the bbox fetches should have covered but didn't, direct-fetches them, and
/// promotes the genuinely bbox-unreliable ones to <c>AlwaysFetchDirect</c>.
///
/// <para>
/// The AWC API omits individual stations from bbox results even when the box
/// is small — the long-standing reason <c>AlwaysFetchDirect</c> exists.
/// Promotion is evidence-gated: a station is bbox-unreliable only when the
/// direct fetch found an observation old enough that the bbox pass
/// <em>should</em> have returned it, but young enough to be inside the bbox's
/// one-hour window.  A just-published observation (a top-of-the-hour race) or
/// a slow-cadence station's 1–3-hour-old report is ingested without promoting,
/// so the direct list grows only from real evidence and never from timing
/// accidents.  Promotion only ever acts on persisted data (saved localities
/// and recipients), never on exploratory lookups — durable side effects attach
/// to committed intent.
/// </para>
/// </summary>
public static class StationGapFiller
{
    // Stations that produced nothing on a direct gap fetch are skipped until
    // this much time passes (in-process; resets on service restart). Most
    // OurAirports-defined airfields near a locality never report a METAR —
    // without a backoff they would be re-fetched fruitlessly every 10-minute
    // cycle forever (WX-140 review).
    private static readonly TimeSpan SilentRetryInterval = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<string, DateTime> _silentUntilUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Finds defined stations within
    /// <see cref="StationCoverage.MaxFallbackDistanceKm"/> of any point (plus
    /// all explicitly named stations, regardless of distance) that lack an
    /// observation inside <see cref="StationCoverage.FreshObservationWindow"/>,
    /// direct-fetches them in batched <c>ids=</c> calls over the same window,
    /// and promotes evidence-confirmed bbox-unreliable stations to
    /// <c>AlwaysFetchDirect</c>.
    /// </summary>
    /// <param name="points">Locality centroids and locality-less recipients' coordinates.</param>
    /// <param name="namedMetarIcaos">Stations explicitly named by localities/recipients — always candidates (a locality's chosen station can sit outside its own centroid's radius; Watonga's KEND is ~68 km out).</param>
    /// <param name="homeIcao">Home station (fetched directly elsewhere; excluded from promotion noise), or <see langword="null"/>.</param>
    /// <param name="dbOptions">EF Core options for candidate, freshness, and promotion queries.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API requests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <sideeffects>HTTP fetches; METAR inserts; sets <c>WxStations.AlwaysFetchDirect</c> on evidence-confirmed stations; log entries.</sideeffects>
    public static async Task FillAsync(
        IReadOnlyList<(double Lat, double Lon)> points,
        IReadOnlyList<string> namedMetarIcaos,
        string? homeIcao,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        if (points.Count == 0 && namedMetarIcaos.Count == 0) return;

        var nowUtc = DateTime.UtcNow;

        // 1. Candidate stations: defined stations near any point, plus named.
        List<(string Icao, double Lat, double Lon)> stations = [];
        if (points.Count > 0)
        {
            await using var db = new WeatherDataContext(dbOptions);

            // Coarse SQL envelope around all points. Latitude and longitude
            // spans are computed separately — 1° of longitude shrinks by
            // cos(lat), so a flat span under-covers east-west anywhere north
            // of ~40°N (WX-140 review; mirrors WxInterpreter's prefilter).
            const double kmPerDegreeLat = 111.0;
            double latSpan = StationCoverage.MaxFallbackDistanceKm / kmPerDegreeLat * 1.1;
            double maxAbsLat = points.Max(p => Math.Abs(p.Lat));
            double cosLat = Math.Cos(maxAbsLat * Math.PI / 180.0);
            double lonSpan = StationCoverage.MaxFallbackDistanceKm / (kmPerDegreeLat * Math.Max(cosLat, 0.01)) * 1.1;
            double minLat = points.Min(p => p.Lat) - latSpan;
            double maxLat = points.Max(p => p.Lat) + latSpan;
            double minLon = points.Min(p => p.Lon) - lonSpan;
            double maxLon = points.Max(p => p.Lon) + lonSpan;

            stations = (await db.WxStations
                .Where(s => s.Lat != null && s.Lon != null
                    && s.Lat >= minLat && s.Lat <= maxLat
                    && s.Lon >= minLon && s.Lon <= maxLon)
                .Select(s => new { s.IcaoId, Lat = s.Lat!.Value, Lon = s.Lon!.Value })
                .ToListAsync(ct))
                .Select(s => (s.IcaoId, s.Lat, s.Lon))
                .ToList();
        }

        var candidates = ObsFetchPlanner.MissingNeighborStations(
            points, StationCoverage.MaxFallbackDistanceKm, stations, namedMetarIcaos, freshIcaos: []);
        if (candidates.Count == 0) return;

        // 2. Freshness: which candidates already have a recent observation?
        // Candidates-first keeps this an index seek on (StationIcao,
        // ObservationUtc) instead of a whole-table scan (WX-140 review).
        List<string> freshIcaos;
        await using (var db = new WeatherDataContext(dbOptions))
        {
            var cutoff = nowUtc - StationCoverage.FreshObservationWindow;
            var candidateList = candidates.ToList();
            freshIcaos = await db.Metars
                .Where(m => candidateList.Contains(m.StationIcao) && m.ObservationUtc >= cutoff)
                .Select(m => m.StationIcao)
                .Distinct()
                .ToListAsync(ct);
        }

        var fresh = new HashSet<string>(freshIcaos, StringComparer.OrdinalIgnoreCase);
        var gaps = candidates
            .Where(icao => !fresh.Contains(icao))
            .Where(icao => !_silentUntilUtc.TryGetValue(icao, out var until) || until <= nowUtc)
            .ToList();
        if (gaps.Count == 0) return;

        Logger.Info($"Station gap fill: {gaps.Count} defined station(s) lack a recent observation: {string.Join(", ", gaps)}");

        // 3. Rescue fetch over the SAME window that defines a gap — a 1-hour
        // fetch against the 3-hour freshness test left slow-cadence stations
        // as permanent phantom gaps (WX-140 review).
        int fetchHours = (int)Math.Ceiling(StationCoverage.FreshObservationWindow.TotalHours);
        var productive = await MetarFetcher.FetchAndInsertByStationsAsync(gaps, dbOptions, httpClient, fetchHours);

        // 4. Backoff bookkeeping: silent stations wait before the next try;
        // productive ones clear (their data now makes them "fresh" anyway).
        var productiveSet = new HashSet<string>(productive.Select(p => p.Icao), StringComparer.OrdinalIgnoreCase);
        foreach (var icao in gaps)
        {
            if (productiveSet.Contains(icao)) _silentUntilUtc.TryRemove(icao, out _);
            else _silentUntilUtc[icao] = nowUtc + SilentRetryInterval;
        }

        // 5. Evidence-gated promotion: bbox-unreliable means the observation
        // existed inside the bbox pass's one-hour window AND predates this
        // fill by enough margin that the bbox should have carried it. Newer
        // (just published — timing race) or older (slow cadence — bbox window
        // legitimately empty) observations are ingested without promoting.
        var promotionFloor = nowUtc.AddMinutes(-65);
        var promotionCeiling = nowUtc.AddMinutes(-5);
        var toPromote = productive
            .Where(p => p.NewestObsUtc >= promotionFloor && p.NewestObsUtc <= promotionCeiling)
            .Select(p => p.Icao)
            .Where(icao => !string.Equals(icao, homeIcao, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toPromote.Count == 0) return;

        await using (var db = new WeatherDataContext(dbOptions))
        {
            var rows = await db.WxStations
                .Where(s => toPromote.Contains(s.IcaoId) && s.AlwaysFetchDirect != true)
                .ToListAsync(ct);
            if (rows.Count == 0) return;

            foreach (var row in rows)
                row.AlwaysFetchDirect = true;
            await db.SaveChangesAsync(ct);
            Logger.Info($"Station gap fill: promoted {rows.Count} bbox-unreliable station(s) to AlwaysFetchDirect: {string.Join(", ", rows.Select(r => r.IcaoId))}");
        }
    }
}