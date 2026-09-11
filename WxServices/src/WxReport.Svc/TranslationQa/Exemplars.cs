using MetarParser.Data.Entities;

using WxInterp;

namespace WxReport.Svc.TranslationQa;

/// <summary>
/// WX-215 — vocabulary-maximizing exemplar pipeline inputs for the WX-214 translation-QA harness.
///
/// Two meteorologically-coherent scenarios — a warm/convective frontal passage and a winter/frozen
/// storm — whose forecast blocks and observations together drive the renderer through (nearly) the
/// whole controlled vocabulary. Each <see cref="Scenario"/> supplies what the real pipeline consumes:
/// a deterministic provisional <see cref="ForecastSnapshotBody"/> (the GFS-derived v1 the reconciler
/// refines), a quieter <see cref="Prior"/> snapshot so the harness's change detector has something to
/// diff against, a primary <see cref="WeatherSnapshot"/> observation for the Reported-Conditions block,
/// and a set of alternate observations — because one report shows one observation, so the observed
/// vocabulary (freezing rain vs snow showers vs heavy rain, fog vs haze, calm vs variable, the
/// low/high ceiling sky variants) can only be swept by re-rendering the same forecast against several
/// observations.
///
/// These are deliberately denser than any single real forecast. Coherence is preserved (the arcs read
/// as real progressions); where a vocabulary cell has no natural place in either arc, it is left out
/// and recorded in COVERAGE.md rather than forced in as token-bingo.
///
/// Times anchor to a UTC locality timezone so a block's <c>StartUtc</c> hour equals its local
/// day-part hour (06 = morning, 12 = afternoon, 18 = evening, 00 = the overnight band) — the same
/// determinism trick the renderer golden corpus uses.
/// </summary>
public static class Exemplars
{
    /// <summary>The locality timezone the scenarios are authored against (UTC → StartUtc hour == local hour).</summary>
    public static readonly TimeZoneInfo LocalityTz = TimeZoneInfo.Utc;

    /// <summary>A named alternate observation: the label feeds the coverage report and the QA artifact.</summary>
    public sealed record NamedObservation(string Label, WeatherSnapshot Observation);

    /// <summary>One exemplar scenario: the pipeline inputs plus the alternate observations that sweep the observed vocabulary.</summary>
    public sealed record Scenario(
        string Name,
        string Synopsis,
        DateTime AnchorDay,
        ForecastSnapshotBody Provisional,
        ForecastSnapshotBody Prior,
        WeatherSnapshot PrimaryObservation,
        IReadOnlyList<NamedObservation> AltObservations)
    {
        /// <summary>
        /// WX-504: the locality timezone this scenario is authored in. Defaults to <see cref="LocalityTz"/>
        /// (UTC), where a block's StartUtc hour IS its local hour — which also makes the UTC→local
        /// conversion a no-op, so a UTC scenario cannot exercise local day-part or day errors.
        /// </summary>
        public TimeZoneInfo Tz { get; init; } = LocalityTz;
    }

    /// <summary>
    /// The translation-QA exemplars. <c>QaRerunWorker</c> judges every one of these with Gemini on each
    /// rerun, so adding a scenario here adds a judged scenario, and its cost, to production QA.
    /// </summary>
    public static IReadOnlyList<Scenario> All() => [WarmConvective(), WinterFrozen()];

    /// <summary>
    /// WX-504: scenarios the dev tool runs by name (<c>--scenario</c>) that are deliberately NOT in
    /// <see cref="All"/>, so production QA reruns never judge them.
    /// </summary>
    public static IReadOnlyList<Scenario> HarnessOnly() => [ChicagoDayParts()];

    /// <summary>Finds a scenario by name across <see cref="All"/> and <see cref="HarnessOnly"/>, or null.</summary>
    public static Scenario? Named(string name) =>
        All().Concat(HarnessOnly())
            .FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

    // ── builders (public mirror of the renderer golden corpus helpers) ───────────────────────────

    /// <summary>Build an observation. Defaults describe a benign, fully-reported station; override per case.</summary>
    public static WeatherSnapshot Obs(
        IReadOnlyList<SkyLayer> sky,
        DateTime obsTimeUtc,
        double tempC,
        double dewC,
        double? visMiles = 10,
        bool visLessThan = false,
        int? windDir = 180, int? windSpd = 10, int? gust = null, bool variable = false,
        IReadOnlyList<SnapshotWeather>? wx = null,
        double altimeterInHg = 29.92,
        bool available = true) => new()
        {
            ObservationAvailable = available,
            LocalityName = "Spring",
            StationMunicipality = "Houston",
            StationName = "David Wayne Hooks Memorial Airport",
            StationIcao = "KDWH",
            ObservationTimeUtc = obsTimeUtc,
            SkyLayers = sky,
            VisibilityStatuteMiles = visMiles,
            VisibilityLessThan = visLessThan,
            WindDirectionDeg = windDir,
            WindSpeedKt = windSpd,
            WindGustKt = gust,
            WindIsVariable = variable,
            WeatherPhenomena = wx ?? [],
            TemperatureCelsius = tempC,
            TemperatureFahrenheit = tempC * 9 / 5 + 32,
            DewPointCelsius = dewC,
            AltimeterInHg = altimeterInHg,
        };

    public static SkyLayer Layer(SkyCoverage cover, int? feet) => new() { Coverage = cover, HeightFeet = feet };

    public static SnapshotWeather Wx(
        WeatherIntensity intensity = WeatherIntensity.Moderate,
        WeatherDescriptor? descriptor = null,
        PrecipitationType[]? precip = null,
        WeatherObscuration? obscuration = null) => new()
        {
            Intensity = intensity,
            Descriptor = descriptor,
            Precipitation = precip ?? [],
            Obscuration = obscuration,
        };

    /// <summary>Build a 6-hour forecast block anchored to a local day-part (StartUtc hour == day-part under the UTC locality tz).</summary>
    public static ForecastSnapshotBlock Blk(
        DateTime startUtc, SkyState sky, PrecipExpectation exp, PrecipPhenomenon? phen,
        (double Min, double Max) tempC, (int Min, int Max) windKt,
        bool severe = false, Obscuration obscuration = Obscuration.None) => new()
        {
            StartUtc = startUtc,
            SkyState = sky,
            Obscuration = obscuration,
            TemperatureCelsius = new(tempC.Min, tempC.Max),
            WindKt = new(windKt.Min, windKt.Max),
            PrecipExpectation = exp,
            PrecipPhenomenon = phen,
            SevereFlag = severe,
        };

    public static ForecastSnapshotBody Fc(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    // ── Scenario A — warm/convective frontal passage (summer) ────────────────────────────────────

    /// <summary>
    /// A classic Gulf-coast warm-sector → cold-frontal passage over ~2.5 days. Airmass storms ahead of
    /// the front build into a severe squall line at the boundary, then post-frontal gusty winds give way
    /// to a cool, dry high. Exercises the convective family (storms possible→likely→expected, severe
    /// storms, non-convective severe-weather wind), pre-frontal rain, clear-and-dry behind the front,
    /// the full sky range, and morning/afternoon/evening/overnight day-parts.
    /// </summary>
    public static Scenario WarmConvective()
    {
        static DateTime D(int day, int hour) => new(2026, 6, day, hour, 0, 0, DateTimeKind.Utc);

        // Chronological ramp (the renderer sorts by StartUtc): pre-frontal rain and airmass storms day 1,
        // a prefrontal squall line that turns severe along the boundary day-2 afternoon/evening, then a
        // cold, dry, gusty post-frontal high day 3.
        var provisional = Fc(
            Blk(D(8, 0), SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Rain, (24, 27), (8, 15)),                  // rain_likely (overnight, pre-frontal)
            Blk(D(8, 12), SkyState.PartlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Thunderstorm, (28, 34), (6, 14)),   // storms_possible (afternoon airmass)
            Blk(D(8, 18), SkyState.MostlyCloudy, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, (26, 31), (8, 16)),     // storms_likely (evening)
            Blk(D(9, 0), SkyState.Overcast, PrecipExpectation.Possible, PrecipPhenomenon.Rain, (23, 27), (10, 18)),               // rain_possible (overnight rain ahead of front)
            Blk(D(9, 6), SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.Thunderstorm, (24, 28), (10, 18)),        // storms_expected (morning squall developing)
            Blk(D(9, 12), SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.Thunderstorm, (25, 30), (15, 28), severe: true), // sev_storms_expected (afternoon squall line)
            Blk(D(9, 18), SkyState.MostlyCloudy, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, (22, 27), (14, 26), severe: true), // sev_storms_likely (evening trailing cells)
            Blk(D(10, 6), SkyState.PartlyCloudy, PrecipExpectation.None, null, (15, 19), (18, 32), severe: true),                 // sev_wx_likely (post-frontal damaging winds, no precip)
            Blk(D(10, 12), SkyState.Clear, PrecipExpectation.None, null, (18, 24), (10, 20)));                                    // clear_and_dry (afternoon)

        // Quieter prior cycle (a benign, mostly-dry outlook): the front was not yet resolved, so the new
        // run's storms/severe register as developing/intensifying and the clearing as ending for the
        // harness's change detector (WX-216 drives the actual diff + change-band).
        var prior = Fc(
            Blk(D(8, 12), SkyState.PartlyCloudy, PrecipExpectation.None, null, (28, 34), (6, 12)),
            Blk(D(8, 18), SkyState.PartlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Rain, (26, 31), (6, 12)),
            Blk(D(9, 12), SkyState.MostlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Thunderstorm, (26, 31), (8, 15)),
            Blk(D(10, 12), SkyState.PartlyCloudy, PrecipExpectation.None, null, (24, 30), (6, 12)));

        // Anchor the report (and the render's "now") at the start of day 1 so no forecast band has
        // elapsed and the whole grid renders — the same determinism the renderer golden corpus uses.
        var anchor = D(8, 0);
        // Primary: a thunderstorm in progress with rain — warm, muggy, gusty, broken low cloud.
        var primary = Obs([Layer(SkyCoverage.Broken, 3500), Layer(SkyCoverage.Overcast, 9000)], anchor,
            tempC: 30, dewC: 24, visMiles: 7, windDir: 160, windSpd: 16, gust: 28,
            wx: [Wx(WeatherIntensity.Moderate, WeatherDescriptor.Thunderstorm, [PrecipitationType.Rain])]);

        var alt = new List<NamedObservation>
        {
            // Observed precip sweep.
            new("light rain showers", Obs([Layer(SkyCoverage.Broken, 4000)], anchor, 27, 22, visMiles: 8, windDir: 170, windSpd: 12,
                wx: [Wx(WeatherIntensity.Light, WeatherDescriptor.Showers, [PrecipitationType.Rain])])),                          // rain_showers
            new("heavy rain", Obs([Layer(SkyCoverage.Overcast, 2500)], anchor, 24, 23, visMiles: 2, windDir: 180, windSpd: 14,
                wx: [Wx(WeatherIntensity.Heavy, precip: [PrecipitationType.Rain])])),                                            // rain_heavy
            new("light rain", Obs([Layer(SkyCoverage.Overcast, 3000)], anchor, 25, 22, visMiles: 6, windDir: 175, windSpd: 10,
                wx: [Wx(WeatherIntensity.Light, precip: [PrecipitationType.Rain])])),                                            // rain_light
            // Obscuration + visibility sweep.
            new("hazy, pre-frontal", Obs([Layer(SkyCoverage.Scattered, 6000)], anchor, 33, 21, visMiles: 4, windDir: 150, windSpd: 8,
                wx: [Wx(obscuration: WeatherObscuration.Haze)])),                                                                // WxHaze + VisHazy
            new("wildfire smoke aloft", Obs([Layer(SkyCoverage.Few, 8000)], anchor, 34, 18, visMiles: 5, windDir: 200, windSpd: 9,
                wx: [Wx(obscuration: WeatherObscuration.Smoke)])),                                                               // WxSmoke
            // Sky/ceiling variants (low/high) + wind states + clear/good.
            new("high broken", Obs([Layer(SkyCoverage.Broken, 25000)], anchor, 32, 19, visMiles: 10, windDir: 190, windSpd: 10)), // SkyMostlycloudyHigh
            new("high overcast", Obs([Layer(SkyCoverage.Overcast, 24000)], anchor, 31, 19, visMiles: 10, windDir: 190, windSpd: 8)), // SkyOvercastHigh
            new("clear and calm", Obs([Layer(SkyCoverage.Clear, null)], anchor, 29, 20, visMiles: 10, windDir: null, windSpd: 0)),  // SkyClear + VisGood + WindCalm
            new("partly cloudy, variable wind", Obs([Layer(SkyCoverage.Scattered, 5000)], anchor, 30, 20, visMiles: 10, windDir: null, windSpd: 5, variable: true)), // SkyPartlyCloudy + WindVariable
            new("mid-deck broken", Obs([Layer(SkyCoverage.Broken, 10000)], anchor, 31, 20, visMiles: 10, windDir: 190, windSpd: 9)), // SkyMostlyCloudy (bare, mid ceiling)
        };

        return new Scenario("warm-convective",
            "Gulf-coast warm sector → severe cold-frontal squall line → cool, dry, gusty post-frontal high (~2.5 days).",
            anchor, provisional, prior, primary, alt);
    }

    // ── Scenario C — local day-parts in a DST-observing zone (WX-504, harness-only) ──────────────

    /// <summary>
    /// WX-504: a Friday-to-Monday outlook authored in America/Chicago, so each block's startUtc differs
    /// from its local hour and the reconciler must name days and day-parts in local time — the step the
    /// WX-340 production watch found failing. Built to bait each failure class that watch measured:
    /// non-severe thunderstorm blocks (storm wording without a severe block); rain confined to Saturday's
    /// early hours after a wet Friday evening (a midnight-crossing window, and an early-hours block
    /// misnamed "morning"); and a weekend dry except early Saturday and Sunday afternoon (a "weekend stays
    /// dry" claim). The one severe block, Sunday evening, keeps the change band on this diagnostic report
    /// (a near-term severe onset) and gives legitimate severe wording something to contrast with.
    /// Not in <see cref="All"/>: production QA never judges it.
    /// </summary>
    public static Scenario ChicagoDayParts()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");
        // A LOCAL wall-clock day-part start in September 2026 (CDT) → its UTC instant.
        DateTime L(int day, int hour) =>
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 9, day, hour, 0, 0, DateTimeKind.Unspecified), tz);

        var provisional = Fc(
            Blk(L(11, 0), SkyState.Clear, PrecipExpectation.None, null, (22, 25), (4, 8)),
            Blk(L(11, 6), SkyState.PartlyCloudy, PrecipExpectation.None, null, (23, 30), (5, 10)),
            Blk(L(11, 12), SkyState.MostlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Thunderstorm, (29, 34), (8, 15)),         // non-severe storm bait
            Blk(L(11, 18), SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, (25, 30), (8, 16)),              // non-severe; opens the midnight-crossing window
            Blk(L(12, 0), SkyState.Overcast, PrecipExpectation.Possible, PrecipPhenomenon.Rain, (22, 25), (6, 12)),                     // rain ONLY in Saturday's early hours
            Blk(L(12, 6), SkyState.PartlyCloudy, PrecipExpectation.None, null, (22, 28), (5, 10)),                                      // Saturday morning is dry
            Blk(L(12, 12), SkyState.Clear, PrecipExpectation.None, null, (28, 33), (5, 10)),
            Blk(L(12, 18), SkyState.Clear, PrecipExpectation.None, null, (25, 29), (3, 8)),
            Blk(L(13, 0), SkyState.Clear, PrecipExpectation.None, null, (21, 24), (3, 6)),
            Blk(L(13, 6), SkyState.PartlyCloudy, PrecipExpectation.None, null, (22, 29), (4, 9)),
            Blk(L(13, 12), SkyState.MostlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Rain, (28, 32), (8, 14)),                // the weekend is NOT all dry
            Blk(L(13, 18), SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, (24, 29), (15, 30), severe: true), // the one severe block
            Blk(L(14, 0), SkyState.PartlyCloudy, PrecipExpectation.None, null, (20, 23), (8, 14)),
            Blk(L(14, 6), SkyState.Clear, PrecipExpectation.None, null, (20, 27), (6, 12)),
            Blk(L(14, 12), SkyState.Clear, PrecipExpectation.None, null, (26, 30), (6, 12)),
            Blk(L(14, 18), SkyState.Clear, PrecipExpectation.None, null, (22, 26), (3, 8)));

        // A quieter prior, so the change detector has onsets to narrate: no Friday storms, no Saturday
        // early-hours rain, no Sunday-afternoon rain, and a non-severe Sunday evening.
        var prior = Fc(
            Blk(L(11, 12), SkyState.PartlyCloudy, PrecipExpectation.None, null, (29, 34), (6, 12)),
            Blk(L(11, 18), SkyState.MostlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Rain, (25, 30), (6, 12)),
            Blk(L(12, 0), SkyState.PartlyCloudy, PrecipExpectation.None, null, (22, 25), (4, 8)),
            Blk(L(13, 12), SkyState.PartlyCloudy, PrecipExpectation.None, null, (28, 32), (6, 12)),
            Blk(L(13, 18), SkyState.MostlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Rain, (24, 29), (8, 15)));

        // "Now" is Friday 00:00 local, so no block has elapsed and the whole grid renders.
        var anchor = L(11, 0);
        var primary = Obs([Layer(SkyCoverage.Clear, null)], anchor, tempC: 24, dewC: 19, windDir: 170, windSpd: 6);

        return new Scenario("chicago-day-parts",
            "America/Chicago, Friday to Monday: non-severe Friday storms, rain only in Saturday's early hours, a partly-wet weekend, one severe Sunday evening.",
            anchor, provisional, prior, primary, [])
        { Tz = tz };
    }

    // ── Scenario B — winter/frozen storm ─────────────────────────────────────────────────────────

    /// <summary>
    /// A winter precipitation event: a warm nose overruns a cold surface layer, so the column starts as
    /// freezing rain/drizzle, transitions through a wintry mix as the warm layer deepens, then changes to
    /// all snow as colder air wins out, before clearing cold and dry. Exercises freezing precip and snow
    /// (possible→likely→expected), the wintry-mix family, low/high overcast, reduced/poor visibility with
    /// fog and mist, calm/variable→strengthening wind, and the temperature-change story.
    /// </summary>
    public static Scenario WinterFrozen()
    {
        static DateTime D(int day, int hour) => new(2026, 1, day, hour, 0, 0, DateTimeKind.Utc);

        // Chronological ramp (the renderer sorts by StartUtc): ice onsets overnight and peaks day-1
        // evening, a warm nose brings a wintry mix overnight into day 2, colder air changes it to snow
        // that peaks day-2 evening, then the storm departs day 3 to a cold, dry clearing.
        var provisional = Fc(
            Blk(D(8, 0), SkyState.Overcast, PrecipExpectation.Possible, PrecipPhenomenon.FreezingPrecip, (-2, 0), (5, 10)),   // fzra_possible (overnight onset)
            Blk(D(8, 12), SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.FreezingPrecip, (-3, -1), (6, 12)),   // fzra_likely (afternoon)
            Blk(D(8, 18), SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.FreezingPrecip, (-4, -2), (8, 14)),  // fzra_expected (evening, peak ice)
            Blk(D(9, 0), SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Mixed, (-3, -1), (10, 16)),            // wmix_likely (overnight, warm nose)
            Blk(D(9, 6), SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.Mixed, (-4, -2), (12, 20)),           // wmix_expected (morning)
            Blk(D(9, 12), SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Snow, (-6, -3), (14, 22)),            // snow_likely (afternoon changeover)
            Blk(D(9, 18), SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.Snow, (-9, -6), (12, 20)),           // snow_expected (evening, heavy snow)
            Blk(D(10, 6), SkyState.MostlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.Snow, (-8, -5), (8, 15)),       // snow_possible (morning flurries)
            Blk(D(10, 12), SkyState.Clear, PrecipExpectation.None, null, (-5, 2), (5, 12)));                                  // clear_and_dry (afternoon, recovering)

        // Quieter prior: the prior run had only a chance of light precip and no organized snow, so the
        // ice/mix/snow register as developing/intensifying and the warming as a temperature change.
        var prior = Fc(
            Blk(D(8, 12), SkyState.MostlyCloudy, PrecipExpectation.Possible, PrecipPhenomenon.FreezingPrecip, (-1, 1), (5, 10)),
            Blk(D(9, 12), SkyState.Overcast, PrecipExpectation.Possible, PrecipPhenomenon.Snow, (-3, -1), (8, 14)),
            Blk(D(10, 12), SkyState.MostlyCloudy, PrecipExpectation.None, null, (-4, 0), (5, 10)));

        // Anchor at the start of day 1 so the full grid renders (no elapsed-band trimming).
        var anchor = D(8, 0);
        // Primary: freezing drizzle with mist under a low ceiling, light/variable wind — the onset.
        var primary = Obs([Layer(SkyCoverage.Overcast, 1500)], anchor,
            tempC: -2, dewC: -3, visMiles: 1.5, windDir: 40, windSpd: 5,
            wx: [Wx(WeatherIntensity.Light, WeatherDescriptor.Freezing, [PrecipitationType.Drizzle]), Wx(obscuration: WeatherObscuration.Mist)]);

        var alt = new List<NamedObservation>
        {
            // Observed frozen-precip sweep.
            new("freezing rain", Obs([Layer(SkyCoverage.Overcast, 2000)], anchor, -3, -4, visMiles: 2, windDir: 50, windSpd: 8,
                wx: [Wx(WeatherIntensity.Moderate, WeatherDescriptor.Freezing, [PrecipitationType.Rain])])),                      // rain_freezing
            new("light drizzle", Obs([Layer(SkyCoverage.Overcast, 1200)], anchor, 1, 0, visMiles: 3, windDir: 60, windSpd: 6,
                wx: [Wx(WeatherIntensity.Light, precip: [PrecipitationType.Drizzle])])),                                         // drizzle_light
            new("sleet / ice pellets (wintry mix)", Obs([Layer(SkyCoverage.Overcast, 2500)], anchor, -2, -3, visMiles: 3, windDir: 40, windSpd: 10,
                wx: [Wx(precip: [PrecipitationType.IcePellets])])),                                                              // wintry_mix
            new("moderate snow", Obs([Layer(SkyCoverage.Overcast, 1800)], anchor, -6, -8, visMiles: 1, windDir: 30, windSpd: 14,
                wx: [Wx(precip: [PrecipitationType.Snow])])),                                                                    // snow
            new("heavy snow", Obs([Layer(SkyCoverage.Overcast, 1500)], anchor, -8, -10, visMiles: 0.3, visLessThan: true, windDir: 30, windSpd: 18, gust: 28,
                wx: [Wx(WeatherIntensity.Heavy, precip: [PrecipitationType.Snow])])),                                            // snow_heavy + VisPoor (no obscuration → distance band)
            new("light snow", Obs([Layer(SkyCoverage.Broken, 3000)], anchor, -7, -9, visMiles: 4, windDir: 20, windSpd: 10,
                wx: [Wx(WeatherIntensity.Light, precip: [PrecipitationType.Snow])])),                                            // snow_light
            new("snow showers", Obs([Layer(SkyCoverage.Broken, 3500)], anchor, -6, -9, visMiles: 3, windDir: 350, windSpd: 12,
                wx: [Wx(WeatherIntensity.Light, WeatherDescriptor.Showers, [PrecipitationType.Snow])])),                         // snow_showers
            // Obscuration + visibility extremes.
            new("dense fog", Obs([Layer(SkyCoverage.VerticalVisibility, 200)], anchor, -1, -1, visMiles: 0.25, visLessThan: true, windDir: null, windSpd: 0,
                wx: [Wx(obscuration: WeatherObscuration.Fog)])),                                                                 // WxFog + VisPoor + WindCalm
            // Sky/ceiling + wind states.
            new("low overcast", Obs([Layer(SkyCoverage.Overcast, 2200)], anchor, -3, -5, visMiles: 8, windDir: 40, windSpd: 12)),  // SkyOvercastLow
            new("high overcast, clearing", Obs([Layer(SkyCoverage.Overcast, 23000)], anchor, -2, -8, visMiles: 10, windDir: 320, windSpd: 14)), // SkyOvercastHigh
            new("cold and clear", Obs([Layer(SkyCoverage.Clear, null)], anchor, -4, -12, visMiles: 10, windDir: null, windSpd: 4, variable: true)), // SkyClear + WindVariable + VisGood
            new("mid-deck overcast", Obs([Layer(SkyCoverage.Overcast, 9000)], anchor, -3, -6, visMiles: 9, windDir: 40, windSpd: 12)), // SkyOvercast (bare, mid ceiling)
        };

        return new Scenario("winter-frozen",
            "Warm-nose overrunning a cold surface layer: freezing rain/drizzle → wintry mix → all snow → clearing cold and dry (~2.5 days).",
            anchor, provisional, prior, primary, alt);
    }
}