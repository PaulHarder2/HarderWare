using MetarParser.Data;
using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace WxInterp;

/// <summary>
/// Queries the database for the most recent METAR and TAF records and
/// produces a language-neutral <see cref="WeatherSnapshot"/>.
/// </summary>
public static class WxInterpreter
{
    /// <summary>
    /// Builds a <see cref="WeatherSnapshot"/> from the most recent METAR and
    /// the most recent valid TAF for <paramref name="tafIcao"/> (if provided),
    /// optionally enriched with a GFS model forecast at the recipient's exact
    /// location.
    /// <para>
    /// METAR stations are tried in the order given by <paramref name="metarIcaos"/>;
    /// only observations within the last 3 hours are accepted, so a stale primary
    /// station falls through to the next candidate.  If no configured station has
    /// recent data the method falls back to the most recent observation from any
    /// station in the database within the same 3-hour window.
    /// </para>
    /// Returns <see langword="null"/> if no METAR data is available at all.
    /// </summary>
    /// <param name="metarIcaos">
    /// Preferred METAR station ICAOs in priority order.  Each is tried in sequence;
    /// a station is only accepted if its most recent observation is within 3 hours.
    /// If none qualify, the method falls back to the most recent station in the
    /// database within the same 3-hour window.  May be empty.
    /// </param>
    /// <param name="tafIcao">
    /// ICAO of the TAF station to include in the snapshot, or
    /// <see langword="null"/> to omit forecast data.
    /// </param>
    /// <param name="localityName">Human-readable location label to embed in the snapshot.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/>.</param>
    /// <param name="homeLat">
    /// Recipient latitude in decimal degrees North, used to query GFS forecast data.
    /// Pass <see langword="null"/> to omit the GFS forecast.
    /// </param>
    /// <param name="homeLon">
    /// Recipient longitude in decimal degrees East (negative = West), used to query
    /// GFS forecast data.  Pass <see langword="null"/> to omit the GFS forecast.
    /// </param>
    /// <param name="precipThresholdMmHr">
    /// Minimum precipitation rate in mm/hr for a GFS forecast hour to contribute to
    /// <see cref="GfsDailyForecast.MaxPrecipRateMmHr"/>.  Ignored when
    /// <paramref name="homeLat"/> or <paramref name="homeLon"/> is <see langword="null"/>.
    /// </param>
    /// <param name="ct">Cancellation token propagated to all EF Core async queries.</param>
    /// <returns>
    /// A populated <see cref="WeatherSnapshot"/>, or <see langword="null"/> if no
    /// METAR data exists in the database at all.
    /// </returns>
    public static async Task<WeatherSnapshot?> GetSnapshotAsync(
        IReadOnlyList<string> metarIcaos,
        string? tafIcao,
        string localityName,
        DbContextOptions<WeatherDataContext> dbOptions,
        double? homeLat = null,
        double? homeLon = null,
        float precipThresholdMmHr = 0.1f,
        CancellationToken ct = default)
    {
        await using var ctx = new WeatherDataContext(dbOptions);

        // Tier 1: try each configured station in preference order, accepting only
        // observations within the last 3 hours.  A stale primary station causes
        // fallthrough to the next candidate rather than surfacing old data.
        var recentCutoff = DateTime.UtcNow.AddHours(-3);
        MetarRecord? metar = null;
        foreach (var icao in metarIcaos)
        {
            metar = await ctx.Metars
                .Include(m => m.SkyConditions)
                .Include(m => m.WeatherPhenomena)
                .Where(m => m.StationIcao == icao && m.ObservationUtc >= recentCutoff)
                .OrderByDescending(m => m.ObservationUtc)
                .FirstOrDefaultAsync(ct);
            if (metar is not null) break;
        }

        // Tier 2: any station in the database with data in the last 3 hours.
        if (metar is null)
        {
            metar = await ctx.Metars
                .Include(m => m.SkyConditions)
                .Include(m => m.WeatherPhenomena)
                .Where(m => m.ObservationUtc >= recentCutoff)
                .OrderByDescending(m => m.ObservationUtc)
                .FirstOrDefaultAsync(ct);
        }

        if (metar is null) return null;

        TafRecord? taf = null;
        if (tafIcao is not null)
        {
            taf = await ctx.Tafs
                .Include(t => t.ChangePeriods)
                    .ThenInclude(p => p.SkyConditions)
                .Include(t => t.ChangePeriods)
                    .ThenInclude(p => p.WeatherPhenomena)
                .Where(t => t.StationIcao == tafIcao && t.ValidToUtc >= DateTime.UtcNow)
                .OrderByDescending(t => t.IssuanceUtc)
                .FirstOrDefaultAsync(ct);
        }

        GfsForecast? gfsForecast = null;
        if (homeLat.HasValue && homeLon.HasValue)
        {
            gfsForecast = await GfsInterpreter.GetForecastAsync(
                homeLat.Value, homeLon.Value, dbOptions, precipThresholdMmHr, ct);
        }

        return BuildSnapshot(metar, taf, localityName, gfsForecast);
    }

    // ── nearest-station resolution ────────────────────────────────────────────

    /// <summary>
    /// Finds the ICAO of the METAR station in the database nearest to the given
    /// coordinates. Uses the Aviation Weather Center airport API to resolve each
    /// station's coordinates. Returns <see langword="null"/> if no stations are found.
    /// </summary>
    /// <param name="lat">Latitude of the target location in decimal degrees.</param>
    /// <param name="lon">Longitude of the target location in decimal degrees.</param>
    /// <param name="dbOptions">EF Core options used to query the list of known stations.</param>
    /// <param name="httpClient">HTTP client used to resolve each station's coordinates via the AWC airport API.</param>
    /// <returns>
    /// The ICAO identifier of the nearest station, or <see langword="null"/> if no
    /// stations have been observed in the last three hours or no coordinates could be resolved.
    /// </returns>
    public static async Task<string?> FindNearestMetarStationAsync(
        double lat, double lon,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var cutoff = DateTime.UtcNow.AddHours(-3);
        await using var ctx = new WeatherDataContext(dbOptions);

        var stations = await ctx.Metars
            .Where(m => m.ObservationUtc >= cutoff)
            .Select(m => m.StationIcao)
            .Distinct()
            .ToListAsync();

        return await FindNearest(stations, lat, lon, httpClient);
    }

    /// <summary>
    /// Finds the ICAO of the TAF station in the database nearest to the given
    /// coordinates. Returns <see langword="null"/> if no stations are found.
    /// </summary>
    /// <param name="lat">Latitude of the target location in decimal degrees.</param>
    /// <param name="lon">Longitude of the target location in decimal degrees.</param>
    /// <param name="dbOptions">EF Core options used to query the list of known stations.</param>
    /// <param name="httpClient">HTTP client used to resolve each station's coordinates via the AWC airport API.</param>
    /// <returns>
    /// The ICAO identifier of the nearest TAF station, or <see langword="null"/> if no
    /// TAFs have been issued in the last 30 hours or no coordinates could be resolved.
    /// </returns>
    public static async Task<string?> FindNearestTafStationAsync(
        double lat, double lon,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var cutoff = DateTime.UtcNow.AddHours(-30);
        await using var ctx = new WeatherDataContext(dbOptions);

        var stations = await ctx.Tafs
            .Where(t => t.IssuanceUtc >= cutoff)
            .Select(t => t.StationIcao)
            .Distinct()
            .ToListAsync();

        return await FindNearest(stations, lat, lon, httpClient);
    }

    /// <summary>
    /// Returns the ICAO from <paramref name="icaos"/> whose airport is closest
    /// to (<paramref name="lat"/>, <paramref name="lon"/>), using squared Euclidean
    /// distance (valid approximation over the small geographic ranges involved).
    /// Stations whose coordinates cannot be resolved via the AWC airport API are
    /// silently skipped.
    /// </summary>
    /// <param name="icaos">Candidate station ICAOs to evaluate.</param>
    /// <param name="lat">Target latitude in decimal degrees.</param>
    /// <param name="lon">Target longitude in decimal degrees.</param>
    /// <param name="httpClient">HTTP client for AWC airport coordinate lookups.</param>
    /// <returns>
    /// The ICAO of the nearest resolvable station, or <see langword="null"/> if
    /// <paramref name="icaos"/> is empty or all coordinate lookups fail.
    /// </returns>
    private static async Task<string?> FindNearest(
        IEnumerable<string> icaos, double lat, double lon, HttpClient httpClient)
    {
        string? nearestIcao = null;
        double  nearestDist = double.MaxValue;

        foreach (var icao in icaos)
        {
            var coords = await AirportLocator.LookupAsync(icao, httpClient);
            if (coords is null) continue;
            var dist = Math.Pow(coords.Value.Latitude  - lat, 2)
                     + Math.Pow(coords.Value.Longitude - lon, 2);
            if (dist < nearestDist) { nearestDist = dist; nearestIcao = icao; }
        }

        return nearestIcao;
    }

    // ── snapshot builder ──────────────────────────────────────────────────────

    /// <summary>
    /// Constructs a <see cref="WeatherSnapshot"/> from raw database entities,
    /// converting all values to the snapshot's canonical units:
    /// wind speed in knots (MPS converted via ×1.94384), visibility in statute miles
    /// (metres converted via ÷1609.344), altimeter in inHg (hPa converted via ÷33.8639),
    /// and temperature in both °C and °F.
    /// </summary>
    /// <param name="metar">The METAR record to use as the current-conditions source.  Must not be <see langword="null"/>.</param>
    /// <param name="taf">The TAF record to populate forecast periods, or <see langword="null"/> to omit forecast data.</param>
    /// <param name="localityName">Human-readable location label to embed in the snapshot.</param>
    /// <param name="gfsForecast">GFS model forecast to attach to the snapshot, or <see langword="null"/> if unavailable.</param>
    /// <returns>A fully populated <see cref="WeatherSnapshot"/> derived from the given records.</returns>
    private static WeatherSnapshot BuildSnapshot(
        MetarRecord metar, TafRecord? taf, string localityName, GfsForecast? gfsForecast = null)
    {
        var visSm    = metar.VisibilityStatuteMiles
                       ?? (metar.VisibilityM.HasValue ? metar.VisibilityM.Value / 1609.344 : null);

        var altInHg  = metar.AltimeterUnit == "inHg"
                       ? metar.AltimeterValue
                       : metar.AltimeterValue.HasValue ? metar.AltimeterValue.Value / 33.8639 : null;

        var tempF    = metar.AirTemperatureCelsius.HasValue
                       ? metar.AirTemperatureCelsius.Value * 9.0 / 5.0 + 32.0
                       : (double?)null;

        var skyLayers = metar.SkyConditions
            .OrderBy(s => s.SortOrder)
            .Select(MapSkyLayer)
            .ToList();

        var phenomena = metar.WeatherPhenomena
            .OrderBy(w => w.SortOrder)
            .Select(w => MapWeather(w, w.PhenomenonKind == "Recent"))
            .ToList();

        var forecastPeriods = taf is not null
            ? taf.ChangePeriods
                .OrderBy(p => p.SortOrder)
                .Select(MapForecastPeriod)
                .ToList()
            : new List<ForecastPeriod>();

        return new WeatherSnapshot
        {
            StationIcao           = metar.StationIcao,
            LocalityName          = localityName,
            ObservationTimeUtc    = metar.ObservationUtc,
            IsAutomated           = metar.IsAuto,
            WindDirectionDeg      = metar.WindDirection,
            WindIsVariable        = metar.WindIsVariable,
            WindSpeedKt           = NormalizeWindKt(metar.WindSpeed, metar.WindUnit),
            WindGustKt            = NormalizeWindKt(metar.WindGust,  metar.WindUnit),
            Cavok                 = metar.VisibilityCavok,
            VisibilityStatuteMiles = visSm,
            VisibilityLessThan    = metar.VisibilityLessThan,
            SkyLayers             = skyLayers,
            WeatherPhenomena      = phenomena,
            TemperatureCelsius    = metar.AirTemperatureCelsius,
            TemperatureFahrenheit = tempF,
            DewPointCelsius       = metar.DewPointCelsius,
            AltimeterInHg         = altInHg,
            TafStationIcao        = taf?.StationIcao,
            ForecastPeriods       = forecastPeriods,
            GfsForecast           = gfsForecast,
        };
    }

    // ── mapping helpers ───────────────────────────────────────────────────────

    /// <summary>Maps a <see cref="MetarSkyCondition"/> database entity to a <see cref="SkyLayer"/> snapshot value.</summary>
    /// <param name="s">The sky condition entity to map.</param>
    /// <returns>A new <see cref="SkyLayer"/> with coverage, height, cloud type, and vertical-visibility flag populated.</returns>
    private static SkyLayer MapSkyLayer(MetarSkyCondition s) => new()
    {
        Coverage             = ParseCoverage(s.Cover),
        HeightFeet           = s.HeightFeet,
        CloudType            = s.CloudType is "CB"  ? CloudType.Cumulonimbus
                             : s.CloudType is "TCU" ? CloudType.ToweringCumulus
                             : CloudType.None,
        IsVerticalVisibility = s.IsVerticalVisibility,
    };

    /// <summary>Maps a <see cref="TafChangePeriodSky"/> database entity to a <see cref="SkyLayer"/> snapshot value.</summary>
    /// <param name="s">The TAF sky condition entity to map.</param>
    /// <returns>A new <see cref="SkyLayer"/> with coverage, height, cloud type, and vertical-visibility flag populated.</returns>
    private static SkyLayer MapTafSkyLayer(TafChangePeriodSky s) => new()
    {
        Coverage             = ParseCoverage(s.Cover),
        HeightFeet           = s.HeightFeet,
        CloudType            = s.CloudType is "CB"  ? CloudType.Cumulonimbus
                             : s.CloudType is "TCU" ? CloudType.ToweringCumulus
                             : CloudType.None,
        IsVerticalVisibility = s.IsVerticalVisibility,
    };

    /// <summary>Maps a <see cref="MetarWeatherPhenomenon"/> database entity to a <see cref="SnapshotWeather"/> value.</summary>
    /// <param name="w">The weather phenomenon entity to map.</param>
    /// <param name="isRecent">
    /// <see langword="true"/> when the phenomenon is from the recent-weather group (RE prefix);
    /// <see langword="false"/> for present weather.
    /// </param>
    /// <returns>A new <see cref="SnapshotWeather"/> with all decoded weather components populated.</returns>
    private static SnapshotWeather MapWeather(MetarWeatherPhenomenon w, bool isRecent) => new()
    {
        Intensity     = ParseIntensity(w.Intensity),
        Descriptor    = ParseDescriptor(w.Descriptor),
        Precipitation = ParsePrecipitation(w.Precipitation),
        Obscuration   = ParseObscuration(w.Obscuration),
        Other         = ParseOther(w.OtherPhenomenon),
        IsRecent      = isRecent,
    };

    /// <summary>Maps a <see cref="TafChangePeriodWeather"/> database entity to a <see cref="SnapshotWeather"/> value.</summary>
    /// <param name="w">The TAF weather phenomenon entity to map.</param>
    /// <returns>A new <see cref="SnapshotWeather"/> with all decoded weather components populated.</returns>
    private static SnapshotWeather MapTafWeather(TafChangePeriodWeather w) => new()
    {
        Intensity     = ParseIntensity(w.Intensity),
        Descriptor    = ParseDescriptor(w.Descriptor),
        Precipitation = ParsePrecipitation(w.Precipitation),
        Obscuration   = ParseObscuration(w.Obscuration),
        Other         = ParseOther(w.OtherPhenomenon),
    };

    /// <summary>Maps a <see cref="TafChangePeriodRecord"/> database entity to a <see cref="ForecastPeriod"/> snapshot value.</summary>
    /// <param name="p">The TAF change period entity to map.</param>
    /// <returns>A new <see cref="ForecastPeriod"/> with change type, validity window, wind, visibility, sky, and weather populated.</returns>
    private static ForecastPeriod MapForecastPeriod(TafChangePeriodRecord p) => new()
    {
        ChangeType             = ParseChangeType(p.ChangeType),
        ValidFromUtc           = p.ValidFromUtc,
        ValidToUtc             = p.ValidToUtc,
        WindDirectionDeg       = p.WindDirection,
        WindIsVariable         = p.WindIsVariable,
        WindSpeedKt            = NormalizeWindKt(p.WindSpeed, p.WindUnit),
        WindGustKt             = NormalizeWindKt(p.WindGust,  p.WindUnit),
        Cavok                  = p.VisibilityCavok,
        VisibilityStatuteMiles = p.VisibilityStatuteMiles
                                 ?? (p.VisibilityM.HasValue ? p.VisibilityM.Value / 1609.344 : null),
        SkyLayers              = p.SkyConditions.OrderBy(s => s.SortOrder).Select(MapTafSkyLayer).ToList(),
        WeatherPhenomena       = p.WeatherPhenomena.OrderBy(w => w.SortOrder).Select(MapTafWeather).ToList(),
    };

    // ── value parsers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises a wind speed value to knots.
    /// Values already in knots (unit "KT") are returned unchanged.
    /// Values in metres per second (unit "MPS") are converted using the factor 1.94384.
    /// </summary>
    /// <param name="speed">Raw wind speed in the original unit, or <see langword="null"/> if not reported.</param>
    /// <param name="unit">Unit string from the decoded report ("KT" or "MPS"); may be <see langword="null"/>.</param>
    /// <returns>Speed in knots, or <see langword="null"/> if <paramref name="speed"/> is <see langword="null"/>.</returns>
    private static int? NormalizeWindKt(int? speed, string? unit) =>
        speed is null ? null
        : unit == "MPS" ? (int)Math.Round(speed.Value * 1.94384)
        : speed;

    private static SkyCoverage ParseCoverage(string s) => s switch
    {
        "SKC" or "CLR" => SkyCoverage.Clear,
        "FEW"          => SkyCoverage.Few,
        "SCT"          => SkyCoverage.Scattered,
        "BKN"          => SkyCoverage.Broken,
        "OVC"          => SkyCoverage.Overcast,
        "VV"           => SkyCoverage.VerticalVisibility,
        "NSC"          => SkyCoverage.NoSignificantCloud,
        "NCD"          => SkyCoverage.NoCloudsDetected,
        _              => SkyCoverage.Clear,
    };

    private static WeatherIntensity ParseIntensity(string s) => s switch
    {
        "+" => WeatherIntensity.Heavy,
        "-" => WeatherIntensity.Light,
        "VC" => WeatherIntensity.InTheVicinity,
        _    => WeatherIntensity.Moderate,
    };

    private static WeatherDescriptor? ParseDescriptor(string? s) => s switch
    {
        "MI" => WeatherDescriptor.Shallow,
        "PR" => WeatherDescriptor.Partial,
        "BC" => WeatherDescriptor.Patches,
        "DR" => WeatherDescriptor.LowDrifting,
        "BL" => WeatherDescriptor.Blowing,
        "SH" => WeatherDescriptor.Showers,
        "TS" => WeatherDescriptor.Thunderstorm,
        "FZ" => WeatherDescriptor.Freezing,
        _    => null,
    };

    private static IReadOnlyList<PrecipitationType> ParsePrecipitation(string? s)
    {
        if (string.IsNullOrEmpty(s)) return [];
        return s.Split(',').Select(p => p.Trim() switch
        {
            "DZ" => PrecipitationType.Drizzle,
            "RA" => PrecipitationType.Rain,
            "SN" => PrecipitationType.Snow,
            "SG" => PrecipitationType.SnowGrains,
            "IC" => PrecipitationType.IceCrystals,
            "PL" => PrecipitationType.IcePellets,
            "GR" => PrecipitationType.Hail,
            "GS" => PrecipitationType.SmallHail,
            _    => PrecipitationType.Unknown,
        }).ToList();
    }

    private static WeatherObscuration? ParseObscuration(string? s) => s switch
    {
        "BR" => WeatherObscuration.Mist,
        "FG" => WeatherObscuration.Fog,
        "FU" => WeatherObscuration.Smoke,
        "VA" => WeatherObscuration.VolcanicAsh,
        "DU" => WeatherObscuration.Dust,
        "SA" => WeatherObscuration.Sand,
        "HZ" => WeatherObscuration.Haze,
        "PY" => WeatherObscuration.Spray,
        _    => null,
    };

    private static OtherPhenomenon? ParseOther(string? s) => s switch
    {
        "PO" => OtherPhenomenon.DustSandWhirls,
        "SQ" => OtherPhenomenon.Squalls,
        "FC" => OtherPhenomenon.FunnelCloud,
        "SS" => OtherPhenomenon.Sandstorm,
        "DS" => OtherPhenomenon.Duststorm,
        _    => null,
    };

    private static ForecastChangeType ParseChangeType(string s) => s switch
    {
        "BASE"          => ForecastChangeType.Base,
        "BECMG"         => ForecastChangeType.BecomeGradually,
        "TEMPO"         => ForecastChangeType.Temporary,
        "FM"            => ForecastChangeType.From,
        "PROB30"        => ForecastChangeType.Probability30,
        "PROB40"        => ForecastChangeType.Probability40,
        "PROB30 TEMPO"  => ForecastChangeType.Probability30Temporary,
        "PROB40 TEMPO"  => ForecastChangeType.Probability40Temporary,
        _               => ForecastChangeType.Base,
    };
}
