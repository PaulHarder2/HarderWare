using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

namespace WxInterp;

/// <summary>
/// Queries the database for the most recent complete GFS model run and produces
/// a <see cref="GfsForecast"/> by bilinear interpolation over the four surrounding
/// 0.25° grid points.
/// </summary>
public static class GfsInterpreter
{
    private const double GridRes = 0.25; // GFS 0.25° horizontal resolution

    /// <summary>
    /// Builds a <see cref="GfsForecast"/> at an exact geographic location by
    /// bilinear interpolation over the four surrounding GFS grid points.
    /// <para>
    /// All 121 forecast hours (f000–f120) are retrieved in a single database query.
    /// Each variable is interpolated independently per hour; results are then grouped
    /// by UTC calendar date to produce per-day summaries.
    /// </para>
    /// Returns <see langword="null"/> when no complete GFS run exists in the database
    /// or no grid data is found near the specified coordinates.
    /// </summary>
    /// <param name="lat">Recipient latitude in decimal degrees North.</param>
    /// <param name="lon">Recipient longitude in decimal degrees East (negative = West).</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/>.</param>
    /// <param name="precipThresholdMmHr">
    /// Minimum precipitation rate in mm/hr for a forecast hour to contribute to
    /// <see cref="GfsDailyForecast.MaxPrecipRateMmHr"/>.  Hours below this threshold
    /// are treated as dry.  Defaults to 0.1 mm/hr.
    /// </param>
    /// <param name="ct">Cancellation token propagated to EF Core queries.</param>
    /// <returns>
    /// A populated <see cref="GfsForecast"/>, or <see langword="null"/> if no data
    /// is available.
    /// </returns>
    public static async Task<GfsForecast?> GetForecastAsync(
        double lat,
        double lon,
        DbContextOptions<WeatherDataContext> dbOptions,
        float precipThresholdMmHr = 0.1f,
        CancellationToken ct = default)
    {
        await using var ctx = new WeatherDataContext(dbOptions);

        // Find the most recent fully-ingested model run.
        var run = await ctx.GfsModelRuns
            .Where(r => r.IsComplete)
            .OrderByDescending(r => r.ModelRunUtc)
            .FirstOrDefaultAsync(ct);

        if (run is null) return null;

        // Compute the four surrounding grid-point coordinates.
        var lat0 = (float)(Math.Floor(lat / GridRes) * GridRes);  // south edge
        var lat1 = (float)(lat0 + GridRes);                        // north edge
        var lon0 = (float)(Math.Floor(lon / GridRes) * GridRes);  // west edge
        var lon1 = (float)(lon0 + GridRes);                        // east edge

        // Single query: all 4 corner points for every forecast hour in this run.
        var rows = await ctx.GfsGrid
            .Where(g => g.ModelRunUtc == run.ModelRunUtc
                     && (g.Lat == lat0 || g.Lat == lat1)
                     && (g.Lon == lon0 || g.Lon == lon1))
            .ToListAsync(ct);

        if (rows.Count == 0) return null;

        // Derive the actual corner coordinates from the returned rows.
        // This avoids float-equality mismatches between computed values and
        // the float values round-tripped through the database.
        var actualLats = rows.Select(r => r.Lat).Distinct().OrderBy(x => x).ToList();
        var actualLons = rows.Select(r => r.Lon).Distinct().OrderBy(x => x).ToList();

        if (actualLats.Count < 2 || actualLons.Count < 2) return null;

        var aLat0 = actualLats[0];
        var aLat1 = actualLats[^1];
        var aLon0 = actualLons[0];
        var aLon1 = actualLons[^1];

        // Bilinear interpolation weights (0 = south/west edge, 1 = north/east edge).
        var t = (aLat1 > aLat0) ? (float)((lat - aLat0) / (aLat1 - aLat0)) : 0.5f;
        var s = (aLon1 > aLon0) ? (float)((lon - aLon0) / (aLon1 - aLon0)) : 0.5f;

        // Interpolate each forecast hour, then summarise by UTC calendar date.
        var hourlyPoints = rows
            .GroupBy(g => g.ForecastHour)
            .Select(g =>
            {
                var sw = g.FirstOrDefault(p => p.Lat == aLat0 && p.Lon == aLon0);
                var se = g.FirstOrDefault(p => p.Lat == aLat0 && p.Lon == aLon1);
                var nw = g.FirstOrDefault(p => p.Lat == aLat1 && p.Lon == aLon0);
                var ne = g.FirstOrDefault(p => p.Lat == aLat1 && p.Lon == aLon1);

                var tmpC = BilerpNullable(sw?.TmpC, se?.TmpC, nw?.TmpC, ne?.TmpC, t, s);
                var uGrd = BilerpNullable(sw?.UGrdMs, se?.UGrdMs, nw?.UGrdMs, ne?.UGrdMs, t, s);
                var vGrd = BilerpNullable(sw?.VGrdMs, se?.VGrdMs, nw?.VGrdMs, ne?.VGrdMs, t, s);
                var pRate = BilerpNullable(sw?.PRateKgM2s, se?.PRateKgM2s, nw?.PRateKgM2s, ne?.PRateKgM2s, t, s);
                var tcdc = BilerpNullable(sw?.TcdcPct, se?.TcdcPct, nw?.TcdcPct, ne?.TcdcPct, t, s);
                var cape = BilerpNullable(sw?.CapeJKg, se?.CapeJKg, nw?.CapeJKg, ne?.CapeJKg, t, s);

                // Derive wind speed (kt) and meteorological direction from U/V components.
                float? windKt = null;
                int? windDir = null;
                if (uGrd.HasValue && vGrd.HasValue)
                {
                    var speedMs = Math.Sqrt((double)uGrd.Value * uGrd.Value + (double)vGrd.Value * vGrd.Value);
                    windKt = (float)(speedMs * 1.94384);
                    // Meteorological convention: direction wind is coming FROM.
                    windDir = (int)((Math.Atan2(-uGrd.Value, -vGrd.Value) * 180.0 / Math.PI + 360.0) % 360.0);
                }

                // Convert precipitation rate from mm/s to mm/hr.
                float? precipMmHr = pRate.HasValue ? pRate.Value * 3600f : null;

                return new HourlyPoint(
                    ValidTime: run.ModelRunUtc.AddHours(g.Key),
                    TmpC: tmpC,
                    WindKt: windKt,
                    WindDir: windDir,
                    PrecipMmHr: precipMmHr,
                    TcdcPct: tcdc,
                    CapeJKg: cape);
            })
            .OrderBy(h => h.ValidTime)
            .ToList();

        var days = hourlyPoints
            .GroupBy(h => DateOnly.FromDateTime(h.ValidTime))
            .OrderBy(g => g.Key)
            .Select(g => BuildDailyForecast(g.Key, g.ToList(), precipThresholdMmHr))
            .ToList();

        return new GfsForecast
        {
            ModelRunUtc = run.ModelRunUtc,
            Days = days,
        };
    }

    // ── daily summary ─────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates a list of hourly interpolated points into a single
    /// <see cref="GfsDailyForecast"/> for the given date.
    /// </summary>
    /// <param name="date">UTC calendar date being summarised.</param>
    /// <param name="hours">All interpolated hourly points that fall on <paramref name="date"/>.</param>
    /// <param name="precipThresholdMmHr">Minimum mm/hr to count as precipitation.</param>
    /// <returns>A <see cref="GfsDailyForecast"/> for the day.</returns>
    private static GfsDailyForecast BuildDailyForecast(
        DateOnly date,
        List<HourlyPoint> hours,
        float precipThresholdMmHr)
    {
        // Temperature high/low.
        var temps = hours.Where(h => h.TmpC.HasValue).Select(h => h.TmpC!.Value).ToList();
        float? highC = temps.Count > 0 ? temps.Max() : null;
        float? lowC = temps.Count > 0 ? temps.Min() : null;

        // Wind: direction taken from the hour of maximum speed.
        var windHours = hours.Where(h => h.WindKt.HasValue).ToList();
        float? maxWindKt = null;
        int? maxWindDir = null;
        if (windHours.Count > 0)
        {
            maxWindKt = windHours.Max(h => h.WindKt!.Value);
            maxWindDir = windHours.First(h => h.WindKt == maxWindKt).WindDir;
        }

        // Cloud cover and CAPE.
        var tcdcValues = hours.Where(h => h.TcdcPct.HasValue).Select(h => h.TcdcPct!.Value).ToList();
        var capeValues = hours.Where(h => h.CapeJKg.HasValue).Select(h => h.CapeJKg!.Value).ToList();
        float? maxTcdc = tcdcValues.Count > 0 ? tcdcValues.Max() : null;
        float? maxCape = capeValues.Count > 0 ? capeValues.Max() : null;

        // Precipitation: only hours at or above the configured threshold.
        var precipValues = hours
            .Where(h => h.PrecipMmHr.HasValue && h.PrecipMmHr.Value >= precipThresholdMmHr)
            .Select(h => h.PrecipMmHr!.Value)
            .ToList();
        float? maxPrecip = precipValues.Count > 0 ? precipValues.Max() : null;

        return new GfsDailyForecast
        {
            Date = date,
            HighTempC = highC,
            HighTempF = highC.HasValue ? (float)(highC.Value * 9.0 / 5.0 + 32.0) : null,
            LowTempC = lowC,
            LowTempF = lowC.HasValue ? (float)(lowC.Value * 9.0 / 5.0 + 32.0) : null,
            MaxWindSpeedKt = maxWindKt,
            DominantWindDirDeg = maxWindDir,
            MaxCloudCoverPct = maxTcdc,
            MaxCapeJKg = maxCape,
            MaxPrecipRateMmHr = maxPrecip,
        };
    }

    // ── interpolation helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Bilinear interpolation over the four corners of a 0.25° grid cell.
    /// Returns <see langword="null"/> when any corner value is unavailable.
    /// </summary>
    /// <param name="sw">South-west corner value.</param>
    /// <param name="se">South-east corner value.</param>
    /// <param name="nw">North-west corner value.</param>
    /// <param name="ne">North-east corner value.</param>
    /// <param name="t">North–south weight (0 = south edge, 1 = north edge).</param>
    /// <param name="s">West–east weight (0 = west edge, 1 = east edge).</param>
    /// <returns>Interpolated value, or null if any corner is null.</returns>
    private static float? BilerpNullable(float? sw, float? se, float? nw, float? ne, float t, float s)
    {
        if (!sw.HasValue || !se.HasValue || !nw.HasValue || !ne.HasValue) return null;
        return (1 - t) * (1 - s) * sw.Value
             + (1 - t) * s * se.Value
             + t * (1 - s) * nw.Value
             + t * s * ne.Value;
    }

    // ── private types ─────────────────────────────────────────────────────────

    /// <summary>Interpolated weather values for a single forecast valid-time.</summary>
    private record struct HourlyPoint(
        DateTime ValidTime,
        float? TmpC,
        float? WindKt,
        int? WindDir,
        float? PrecipMmHr,
        float? TcdcPct,
        float? CapeJKg);
}