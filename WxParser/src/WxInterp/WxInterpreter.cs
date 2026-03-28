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
    /// the most recent valid TAF for <paramref name="tafIcao"/> (if provided).
    /// <para>
    /// METAR stations are tried in the order given by <paramref name="metarIcaos"/>.
    /// If none have recent data the method falls back to the most recent METAR
    /// from any station in the database (last-resort, same cycle only).
    /// </para>
    /// Returns <see langword="null"/> if no METAR data is available at all.
    /// </summary>
    public static async Task<WeatherSnapshot?> GetSnapshotAsync(
        IReadOnlyList<string> metarIcaos,
        string? tafIcao,
        string localityName,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        await using var ctx = new WeatherDataContext(dbOptions);

        // Tier 1: try each configured station in preference order.
        MetarRecord? metar = null;
        foreach (var icao in metarIcaos)
        {
            metar = await ctx.Metars
                .Include(m => m.SkyConditions)
                .Include(m => m.WeatherPhenomena)
                .Where(m => m.StationIcao == icao)
                .OrderByDescending(m => m.ObservationUtc)
                .FirstOrDefaultAsync();
            if (metar is not null) break;
        }

        // Tier 2: any station in the database with data in the last 3 hours.
        if (metar is null)
        {
            var cutoff = DateTime.UtcNow.AddHours(-3);
            metar = await ctx.Metars
                .Include(m => m.SkyConditions)
                .Include(m => m.WeatherPhenomena)
                .Where(m => m.ObservationUtc >= cutoff)
                .OrderByDescending(m => m.ObservationUtc)
                .FirstOrDefaultAsync();
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
                .FirstOrDefaultAsync();
        }

        return BuildSnapshot(metar, taf, localityName);
    }

    // ── nearest-station resolution ────────────────────────────────────────────

    /// <summary>
    /// Finds the ICAO of the METAR station in the database nearest to the given
    /// coordinates. Uses the Aviation Weather Center airport API to resolve each
    /// station's coordinates. Returns <see langword="null"/> if no stations are found.
    /// </summary>
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

    private static WeatherSnapshot BuildSnapshot(
        MetarRecord metar, TafRecord? taf, string localityName)
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
        };
    }

    // ── mapping helpers ───────────────────────────────────────────────────────

    private static SkyLayer MapSkyLayer(MetarSkyCondition s) => new()
    {
        Coverage             = ParseCoverage(s.Cover),
        HeightFeet           = s.HeightFeet,
        CloudType            = s.CloudType is "CB"  ? CloudType.Cumulonimbus
                             : s.CloudType is "TCU" ? CloudType.ToweringCumulus
                             : CloudType.None,
        IsVerticalVisibility = s.IsVerticalVisibility,
    };

    private static SkyLayer MapTafSkyLayer(TafChangePeriodSky s) => new()
    {
        Coverage             = ParseCoverage(s.Cover),
        HeightFeet           = s.HeightFeet,
        CloudType            = s.CloudType is "CB"  ? CloudType.Cumulonimbus
                             : s.CloudType is "TCU" ? CloudType.ToweringCumulus
                             : CloudType.None,
        IsVerticalVisibility = s.IsVerticalVisibility,
    };

    private static SnapshotWeather MapWeather(MetarWeatherPhenomenon w, bool isRecent) => new()
    {
        Intensity     = ParseIntensity(w.Intensity),
        Descriptor    = ParseDescriptor(w.Descriptor),
        Precipitation = ParsePrecipitation(w.Precipitation),
        Obscuration   = ParseObscuration(w.Obscuration),
        Other         = ParseOther(w.OtherPhenomenon),
        IsRecent      = isRecent,
    };

    private static SnapshotWeather MapTafWeather(TafChangePeriodWeather w) => new()
    {
        Intensity     = ParseIntensity(w.Intensity),
        Descriptor    = ParseDescriptor(w.Descriptor),
        Precipitation = ParsePrecipitation(w.Precipitation),
        Obscuration   = ParseObscuration(w.Obscuration),
        Other         = ParseOther(w.OtherPhenomenon),
    };

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
