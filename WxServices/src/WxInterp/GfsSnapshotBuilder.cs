using MetarParser.Data.Entities;

using WxServices.Common;

namespace WxInterp;

/// <summary>
/// Pass 1 of the WX-47 two-pass forecast protocol: deterministic projection of a
/// GFS hourly forecast into the uniform 6-hour-block snapshot defined by WX-76.
/// No TAF, no observation, no judgment — purely mechanical mapping with the
/// thresholds documented below.
///
/// <para>
/// Output is a <see cref="ForecastSnapshotBody"/>.  Persistence as a
/// <see cref="ForecastSnapshot"/> entity (with station identifier and generation
/// timestamp) is the caller's responsibility under WX-78.
/// </para>
///
/// <para>
/// The thunderstorm gate intentionally uses CAPE alone for v1; refinement with
/// CIN to suppress capped-environment false positives is tracked in WX-87.
/// </para>
/// </summary>
public static class GfsSnapshotBuilder
{
    // ── thresholds ───────────────────────────────────────────────────────────
    //
    // Threshold provenance is the WX-77 grooming conversation with Paul (former
    // weather forecaster).  Edits to any value should be reviewed against the
    // unit tests in GfsSnapshotBuilderTests, which encode the boundary cases.

    /// <summary>Precip rate at or above which a forecast hour counts as "wet" (mm/hr).</summary>
    private const float WetThresholdMmHr = 0.1f;
    /// <summary>Precip rate at or above which a forecast hour counts as "heavy" (mm/hr).</summary>
    private const float HeavyThresholdMmHr = 2.5f;

    /// <summary>CAPE at or above which a wet hour is considered convective (thunderstorm gate).</summary>
    private const float ThunderstormCapeJKg = 1000f;
    /// <summary>CAPE at or above which a wet hour is considered severe-capable.</summary>
    private const float SevereCapeJKg = 2500f;

    /// <summary>Maximum cloud-cover percentage that still maps to "clear".</summary>
    private const float SkyClearMaxPct = 20f;
    /// <summary>Maximum cloud-cover percentage that still maps to "partly cloudy".</summary>
    private const float SkyPartlyMaxPct = 60f;
    /// <summary>Maximum cloud-cover percentage that still maps to "mostly cloudy"; anything higher is "overcast".</summary>
    private const float SkyMostlyMaxPct = 87f;

    /// <summary>Surface-temperature ceiling for a wet hour to be classified as snow (°C).</summary>
    private const float SnowMaxTmpC = -1f;
    /// <summary>Lower bound of the surface-temperature band that admits freezing precipitation (°C, inclusive).</summary>
    private const float FreezingPrecipTmpLowC = -1f;
    /// <summary>Upper bound of the surface-temperature band that admits freezing precipitation (°C, inclusive).</summary>
    private const float FreezingPrecipTmpHighC = 1f;
    /// <summary>Dew-point ceiling that signals freezing precipitation when paired with a near-zero surface temperature (°C).</summary>
    private const float FreezingPrecipDwpMaxC = -1f;

    /// <summary>Minimum number of hours of data required for a 6-hour block to be emitted.</summary>
    private const int MinHoursPerBlock = 4;
    /// <summary>Maximum number of 6-hour blocks emitted in one snapshot (six-day horizon).</summary>
    private const int MaxBlocks = 24;
    /// <summary>Length of a snapshot block in hours. Public so consumers that reason about a block's time
    /// extent (e.g. the WX-156 severe subject prefix's in-window test) reference this one definition rather
    /// than re-hardcoding the duration — single source of truth.</summary>
    public const int BlockHours = 6;

    // ── public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a provisional <see cref="ForecastSnapshotBody"/> from a GFS hourly
    /// forecast.  Hours are grouped into 6-hour blocks aligned to the locality's
    /// local day-parts (00/06/12/18 local: night/morning/afternoon/evening) per
    /// <paramref name="tz"/>; blocks with fewer than <see cref="MinHoursPerBlock"/>
    /// hours of data or no temperature signal are skipped.  Up to
    /// <see cref="MaxBlocks"/> blocks are emitted.
    /// </summary>
    /// <param name="forecast">The hourly GFS forecast to project.</param>
    /// <param name="tz">The locality timezone whose local day-parts define the block boundaries (WX-155).  Required — pass <see cref="TimeZoneInfo.Utc"/> for the pre-WX-155 UTC-aligned grid.</param>
    /// <returns>A schema-version-1 body whose blocks may be persisted as-is or refined by Pass 2 (WX-79).</returns>
    public static ForecastSnapshotBody Build(GfsHourlyForecast forecast, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(tz);

        var blocks = forecast.Hours
            .GroupBy(h => FloorToLocalDayPartStart(h.ValidTimeUtc, tz))
            .OrderBy(g => g.Key)
            .Select(g => TryBuildBlock(g.Key, [.. g]))
            .Where(b => b is not null)
            .Select(b => b!)
            .Take(MaxBlocks)
            .ToList();

        return new ForecastSnapshotBody { Blocks = blocks };
    }

    // ── block builder ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds one <see cref="ForecastSnapshotBlock"/>, or returns <see langword="null"/>
    /// if the block has too few hours of data or no temperature signal to be
    /// honest about.
    /// </summary>
    private static ForecastSnapshotBlock? TryBuildBlock(DateTime startUtc, List<GfsHourlyPoint> hours)
    {
        if (hours.Count < MinHoursPerBlock) return null;

        var tempValues = hours.Where(h => h.TmpC.HasValue).Select(h => (double)h.TmpC!.Value).ToList();
        if (tempValues.Count == 0) return null;

        var windValues = hours.Where(h => h.WindKt.HasValue).Select(h => h.WindKt!.Value).ToList();
        var tcdcValues = hours.Where(h => h.TcdcPct.HasValue).Select(h => h.TcdcPct!.Value).ToList();
        var capeValues = hours.Where(h => h.CapeJKg.HasValue).Select(h => h.CapeJKg!.Value).ToList();

        var wetHours = hours.Count(h => h.PrecipMmHr.HasValue && h.PrecipMmHr.Value >= WetThresholdMmHr);
        var heavyHours = hours.Count(h => h.PrecipMmHr.HasValue && h.PrecipMmHr.Value >= HeavyThresholdMmHr);

        var precipExpectation = DerivePrecipExpectation(wetHours, heavyHours);
        PrecipPhenomenon? phenomenon = null;
        if (precipExpectation != PrecipExpectation.None)
        {
            phenomenon = DerivePhenomenon(hours);
        }

        var windMin = windValues.Count > 0 ? (int)Math.Round(windValues.Min(), MidpointRounding.AwayFromZero) : 0;
        var windMax = windValues.Count > 0 ? (int)Math.Round(windValues.Max(), MidpointRounding.AwayFromZero) : 0;

        return new ForecastSnapshotBlock
        {
            StartUtc = startUtc,
            SkyState = DeriveSkyState(tcdcValues),
            Obscuration = Obscuration.None,
            TemperatureCelsius = new MinMax<double>(
                Math.Round(tempValues.Min(), 1),
                Math.Round(tempValues.Max(), 1)),
            WindKt = new MinMax<int>(windMin, windMax),
            PrecipExpectation = precipExpectation,
            PrecipPhenomenon = phenomenon,
            SevereFlag = DeriveSevereFlag(windValues, capeValues, wetHours),
        };
    }

    // ── derivations ──────────────────────────────────────────────────────────

    private static SkyState DeriveSkyState(List<float> tcdcValues)
    {
        if (tcdcValues.Count == 0) return SkyState.MostlyCloudy;
        var max = tcdcValues.Max();
        if (max <= SkyClearMaxPct) return SkyState.Clear;
        if (max <= SkyPartlyMaxPct) return SkyState.PartlyCloudy;
        if (max <= SkyMostlyMaxPct) return SkyState.MostlyCloudy;
        return SkyState.Overcast;
    }

    private static PrecipExpectation DerivePrecipExpectation(int wetHours, int heavyHours)
    {
        if (wetHours == 0) return PrecipExpectation.None;
        if (wetHours >= 5 || heavyHours >= 2) return PrecipExpectation.Certain;
        if (wetHours >= 2 || heavyHours >= 1) return PrecipExpectation.Likely;
        return PrecipExpectation.Possible;
    }

    private static PrecipPhenomenon DerivePhenomenon(List<GfsHourlyPoint> hours)
    {
        var wetHours = hours
            .Where(h => h.PrecipMmHr.HasValue && h.PrecipMmHr.Value >= WetThresholdMmHr)
            .ToList();

        if (wetHours.Any(h => h.CapeJKg.HasValue && h.CapeJKg.Value >= ThunderstormCapeJKg))
            return PrecipPhenomenon.Thunderstorm;

        var wetWithTemp = wetHours.Where(h => h.TmpC.HasValue).ToList();
        if (wetWithTemp.Count > 0 && wetWithTemp.All(h => h.TmpC!.Value <= SnowMaxTmpC))
            return PrecipPhenomenon.Snow;

        if (wetHours.Any(h =>
            h.TmpC.HasValue
            && h.TmpC.Value >= FreezingPrecipTmpLowC
            && h.TmpC.Value <= FreezingPrecipTmpHighC
            && h.DwpC.HasValue
            && h.DwpC.Value <= FreezingPrecipDwpMaxC))
            return PrecipPhenomenon.FreezingPrecip;

        var anyFrozenWet = wetHours.Any(h => h.TmpC.HasValue && h.TmpC.Value < 0);
        var anyThawedWet = wetHours.Any(h => h.TmpC.HasValue && h.TmpC.Value > 0);
        if (anyFrozenWet && anyThawedWet)
            return PrecipPhenomenon.Mixed;

        return PrecipPhenomenon.Rain;
    }

    private static bool DeriveSevereFlag(List<float> windValues, List<float> capeValues, int wetHours)
    {
        var maxWind = windValues.Count > 0 ? windValues.Max() : 0f;
        var maxCape = capeValues.Count > 0 ? capeValues.Max() : 0f;
        return maxWind >= WxThresholds.SevereWindKt
            || (maxCape >= SevereCapeJKg && wetHours > 0);
    }

    // ── alignment ────────────────────────────────────────────────────────────

    // WX-155: blocks are bucketed by the LOCALITY'S local day-part, not the UTC
    // 6-hour grid. A UTC-aligned block straddles two local day-parts for any
    // offset timezone (12Z–18Z is 7 AM–1 PM in CDT — five hours of morning plus
    // one of afternoon), which let day-part prose contradict its own clock. Local
    // day-parts: 00–05 night, 06–11 morning, 12–17 afternoon, 18–23 evening. A
    // 6-hour boundary can be an invalid (spring-forward gap) or ambiguous
    // (fall-back) local time in a zone whose DST transition lands on it — US zones
    // transition at 02:00, but others (e.g. midnight-transition zones like
    // America/Santiago) do not — so a gap boundary is stepped forward one hour to
    // the first real instant (ConvertTimeToUtc otherwise throws), and an ambiguous
    // boundary is resolved deterministically by ConvertTimeToUtc. Every hour of a
    // day-part still maps to one key, so grouping is unaffected; a block's hour
    // count varies by ±1 across a transition. StartUtc stays a UTC instant — only
    // its anchoring changed.
    private static DateTime FloorToLocalDayPartStart(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var flooredHour = local.Hour - (local.Hour % BlockHours);
        var localStart = new DateTime(local.Year, local.Month, local.Day, flooredHour, 0, 0, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(localStart))
            localStart = localStart.AddHours(1);  // the spring-forward gap is 1 h; step to the first real instant
        return TimeZoneInfo.ConvertTimeToUtc(localStart, tz);
    }
}