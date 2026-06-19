using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

namespace WxReport.Tests.Golden;

/// <summary>
/// WX-171 no-regression corpus. A deterministic matrix of renderer inputs whose
/// union exercises every renderer-reachable <c>LanguageTemplates</c> token, in
/// both en and es. Each scenario renders a full report (or welcome / degraded);
/// the golden harness (<see cref="RendererGoldenTests"/>) records the output of
/// the CURRENT (pre-rewire) renderer, then — after the DB-template rewire —
/// asserts the rewired renderer reproduces it byte-for-byte, bar documented
/// intended deltas. Times use a UTC locality timezone so local calendar days
/// equal UTC days (deterministic).
/// </summary>
public static class RendererGoldenCorpus
{
    public readonly record struct GoldenCase(string Name, string Content);

    private static readonly DateTime Anchor = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc);
    // Send instant (nowUtc) for the corpus: midnight at the start of the forecast day, so no
    // forecast band has elapsed under WX-195's trimming and every grid cell renders —
    // maximizing token coverage. Fixed for determinism.
    private static readonly DateTime Now = new(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly int[] ScheduleHours = [6, 18];

    // WX-171: the renderer now reads atomic phrases from the DB-backed template store; the
    // corpus builds one from the migration seed (the same rows production loads) and resolves
    // each language's TemplateSet + culture, so the goldens exercise the real DB phrases.
    private static readonly LanguageTemplateStore Store = SeedTemplateStore.Build();
    private static TemplateSet Templates(string lang) => Store.ForLanguage(lang);
    private static System.Globalization.CultureInfo Culture(string lang) => Store.CultureFor(lang);

    // ── recipients (units only; language is a separate render arg) ──────────────

    private static Recipient Imperial() => new()
    {
        RecipientId = "g-imp",
        Name = "Pat",
        Email = "p@example.com",
        TempUnit = "F",
        PressureUnit = "inHg",
        WindSpeedUnit = "mph",
        PrecipUnit = "in",
    };

    private static Recipient Metric() => new()
    {
        RecipientId = "g-met",
        Name = "Pat",
        Email = "p@example.com",
        TempUnit = "C",
        PressureUnit = "kPa",
        WindSpeedUnit = "kph",
        PrecipUnit = "mm",
    };

    // ── observation + forecast builders ─────────────────────────────────────────

    private static WeatherSnapshot Obs(
        IReadOnlyList<SkyLayer> sky,
        double? visMiles = 10,
        int? windDir = 180, int? windSpd = 12, int? gust = 22, bool variable = false,
        IReadOnlyList<SnapshotWeather>? wx = null,
        bool available = true) => new()
        {
            ObservationAvailable = available,
            LocalityName = "Spring",
            StationMunicipality = "Houston",
            StationName = "David Wayne Hooks Memorial Airport",
            StationIcao = "KDWH",
            ObservationTimeUtc = Anchor,
            SkyLayers = sky,
            VisibilityStatuteMiles = visMiles,
            WindDirectionDeg = windDir,
            WindSpeedKt = windSpd,
            WindGustKt = gust,
            WindIsVariable = variable,
            WeatherPhenomena = wx ?? [],
            TemperatureCelsius = 31.0,
            TemperatureFahrenheit = 87.8,
            DewPointCelsius = 24.0,
            AltimeterInHg = 29.92,
        };

    private static SkyLayer Layer(SkyCoverage cover, int? feet) => new() { Coverage = cover, HeightFeet = feet };

    private static SnapshotWeather Wx(
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

    private static ForecastSnapshotBlock Blk(
        int hourUtc, SkyState sky, PrecipExpectation exp, PrecipPhenomenon? phen,
        bool severe = false, int day = 8) => new()
        {
            StartUtc = new(2026, 6, day, hourUtc, 0, 0, DateTimeKind.Utc),
            SkyState = sky,
            Obscuration = Obscuration.None,
            TemperatureCelsius = new(24, 31),
            WindKt = new(5, 12),
            PrecipExpectation = exp,
            PrecipPhenomenon = phen,
            SevereFlag = severe,
        };

    private static ForecastSnapshotBody Fc(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    // Closing prose carrying one of every quantity-token kind + a time token.
    private const string ClosingEn =
        "Highs near {q:temp:33.5}, winds to {q:wind:22} gusting {q:gust:30}, pressure {q:pressure:1013.2}, rainfall {q:precip_mm:12}, clearing after {q:time:2026-06-08T21:00:00Z}.";
    private const string ClosingEs =
        "Máximas cerca de {q:temp:33.5}, lluvia de {q:precip_mm:12}, despejando tras {q:time:2026-06-08T21:00:00Z}.";

    private static StructuredReportBody Body(bool withChange) => new()
    {
        Changes = withChange
            ? [new ReportChange { Tier = ChangeTier.Plans, Phenomenon = ChangePhenomenon.Thunderstorm, Direction = ChangeDirection.Appearing, Window = new(Anchor, Anchor.AddHours(6)), Quantities = [], SummaryToken = "ch1" }]
            : [],
        Narrative = new Dictionary<string, NarrativeSections>
        {
            ["en"] = new() { ChangeSummary = withChange ? "{ch1}A line of storms is moving in this evening." : null, Closing = ClosingEn },
            ["es"] = new() { ChangeSummary = withChange ? "{ch1}Una línea de tormentas se acerca esta noche." : null, Closing = ClosingEs },
        },
    };

    private static string Report(WeatherSnapshot obs, ForecastSnapshotBody fc, Recipient r, string lang, ReportKind kind, bool withChange = false) =>
        StructuredReportRenderer.Render(Body(withChange), fc, obs, r, Templates(lang), Culture(lang), Utc, kind, Now);

    // ── shared fixtures ─────────────────────────────────────────────────────────

    private static WeatherSnapshot ObsLowOvercastLightRain() =>
        Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(WeatherIntensity.Light, precip: [PrecipitationType.Rain])]);

    private static WeatherSnapshot ObsClear() => Obs([Layer(SkyCoverage.Clear, null)], wx: []);

    // Today: afternoon storms (likely) then evening rain (certain → "expected").
    private static ForecastSnapshotBody FcStormsThenRain() =>
        Fc(Blk(12, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm),
           Blk(18, SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.Rain));

    private static ForecastSnapshotBody FcClearDry() =>
        Fc(Blk(12, SkyState.Clear, PrecipExpectation.None, null),
           Blk(18, SkyState.Clear, PrecipExpectation.None, null));

    private static ForecastSnapshotBody FcDry(SkyState sky) =>
        Fc(Blk(12, sky, PrecipExpectation.None, null),
           Blk(18, sky, PrecipExpectation.None, null));

    // One phenomenon swept across possible/likely/expected on three SEPARATE days
    // (non-contiguous, so the episode builder never merges them) — one afternoon
    // condition cell per outlook. severe=true routes through the severe clause.
    private static ForecastSnapshotBody FcOutlooks(PrecipPhenomenon phen, bool severe = false) =>
        Fc(Blk(12, SkyState.Overcast, PrecipExpectation.Possible, phen, severe, day: 8),
           Blk(12, SkyState.Overcast, PrecipExpectation.Likely, phen, severe, day: 9),
           Blk(12, SkyState.Overcast, PrecipExpectation.Certain, phen, severe, day: 10));

    // ── scenarios (rendered in both en and es) ──────────────────────────────────

    private sealed record Scenario(string Name, Func<Recipient, string, string> Render);

    private static IEnumerable<Scenario> Scenarios()
    {
        // Full report: change band + closing + low overcast + light rain + storms/rain grid.
        yield return new("rich", (r, l) => Report(ObsLowOvercastLightRain(), FcStormsThenRain(), r, l, ReportKind.Scheduled, withChange: true));
        yield return new("unscheduled", (r, l) => Report(ObsLowOvercastLightRain(), FcStormsThenRain(), r, l, ReportKind.Unscheduled, withChange: true));
        yield return new("diagnostic", (r, l) => Report(ObsClear(), FcClearDry(), r, l, ReportKind.Diagnostic));

        // Forecast-grid condition coverage.
        yield return new("clear-dry", (r, l) => Report(ObsClear(), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("overcast-dry", (r, l) => Report(ObsClear(), FcDry(SkyState.Overcast), r, l, ReportKind.Scheduled));
        yield return new("partly-dry", (r, l) => Report(ObsClear(), FcDry(SkyState.PartlyCloudy), r, l, ReportKind.Scheduled));
        yield return new("mostly-dry", (r, l) => Report(ObsClear(), FcDry(SkyState.MostlyCloudy), r, l, ReportKind.Scheduled));
        // Outlook sweeps (possible/likely/expected) per phenomenon — non-severe grid cells.
        yield return new("storms-outlooks", (r, l) => Report(ObsClear(), FcOutlooks(PrecipPhenomenon.Thunderstorm), r, l, ReportKind.Scheduled));
        yield return new("snow-outlooks", (r, l) => Report(ObsClear(), FcOutlooks(PrecipPhenomenon.Snow), r, l, ReportKind.Scheduled));
        yield return new("wmix-outlooks", (r, l) => Report(ObsClear(), FcOutlooks(PrecipPhenomenon.Mixed), r, l, ReportKind.Scheduled));
        yield return new("fzra-outlooks", (r, l) => Report(ObsClear(), FcOutlooks(PrecipPhenomenon.FreezingPrecip), r, l, ReportKind.Scheduled));
        // Severe convective swept across outlooks (sev_storms_*), and severe timing
        // in the morning + overnight (CondMorning / CondOvernight when-words).
        yield return new("severe-outlooks", (r, l) => Report(ObsClear(), FcOutlooks(PrecipPhenomenon.Thunderstorm, severe: true), r, l, ReportKind.Scheduled));
        yield return new("severe-timing", (r, l) => Report(ObsClear(),
            Fc(Blk(6, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, severe: true, day: 8),
               Blk(0, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, severe: true, day: 9)), r, l, ReportKind.Scheduled));
        yield return new("rain-snow", (r, l) => Report(ObsClear(),
            Fc(Blk(0, SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.Snow),
               Blk(6, SkyState.Overcast, PrecipExpectation.Possible, PrecipPhenomenon.Rain)), r, l, ReportKind.Scheduled));
        yield return new("wintry-freezing", (r, l) => Report(ObsClear(),
            Fc(Blk(12, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Mixed),
               Blk(18, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.FreezingPrecip)), r, l, ReportKind.Scheduled));
        yield return new("spanning", (r, l) => Report(ObsClear(),
            Fc(Blk(6, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Rain),
               Blk(12, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Rain)), r, l, ReportKind.Scheduled));
        yield return new("severe-convective", (r, l) => Report(ObsClear(),
            Fc(Blk(12, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, severe: true),
               Blk(18, SkyState.Overcast, PrecipExpectation.Certain, PrecipPhenomenon.Rain)), r, l, ReportKind.Scheduled));
        yield return new("severe-noprecip", (r, l) => Report(ObsClear(),
            Fc(Blk(12, SkyState.MostlyCloudy, PrecipExpectation.None, null, severe: true)), r, l, ReportKind.Scheduled));

        // Current-conditions sky coverage (low/high prefixes + obscured).
        yield return new("sky-high-overcast", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 25000)], wx: []), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("sky-mostly-low", (r, l) => Report(Obs([Layer(SkyCoverage.Broken, 4000)], wx: []), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("sky-mostly-high", (r, l) => Report(Obs([Layer(SkyCoverage.Broken, 25000)], wx: []), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("sky-partly", (r, l) => Report(Obs([Layer(SkyCoverage.Scattered, 5000)], wx: []), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("sky-obscured", (r, l) => Report(Obs([Layer(SkyCoverage.VerticalVisibility, 200)], wx: []), FcClearDry(), r, l, ReportKind.Scheduled));

        // Wind.
        yield return new("wind-calm", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], windDir: null, windSpd: 0, gust: null, wx: []), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wind-variable", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], windDir: null, windSpd: 5, gust: null, variable: true, wx: []), FcClearDry(), r, l, ReportKind.Scheduled));

        // Visibility bands.
        yield return new("vis-hazy", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], visMiles: 3, wx: []), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("vis-reduced", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], visMiles: 1, wx: []), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("vis-poor", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], visMiles: 0.25, wx: []), FcClearDry(), r, l, ReportKind.Scheduled));

        // Current-conditions present weather.
        yield return new("wx-heavy-rain", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(WeatherIntensity.Heavy, precip: [PrecipitationType.Rain])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-moderate-rain", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(WeatherIntensity.Moderate, precip: [PrecipitationType.Rain])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-drizzle", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(WeatherIntensity.Light, precip: [PrecipitationType.Drizzle])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-snow", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(WeatherIntensity.Heavy, precip: [PrecipitationType.Snow])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-freezing-rain", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(descriptor: WeatherDescriptor.Freezing, precip: [PrecipitationType.Rain])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-freezing-drizzle", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(descriptor: WeatherDescriptor.Freezing, precip: [PrecipitationType.Drizzle])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-rain-showers", (r, l) => Report(Obs([Layer(SkyCoverage.Broken, 4000)], wx: [Wx(descriptor: WeatherDescriptor.Showers, precip: [PrecipitationType.Rain])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-snow-showers", (r, l) => Report(Obs([Layer(SkyCoverage.Broken, 4000)], wx: [Wx(descriptor: WeatherDescriptor.Showers, precip: [PrecipitationType.Snow])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-wintry", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(precip: [PrecipitationType.IcePellets])]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("wx-thunderstorm", (r, l) => Report(Obs([Layer(SkyCoverage.Overcast, 3000)], wx: [Wx(descriptor: WeatherDescriptor.Thunderstorm, precip: [PrecipitationType.Rain])]), FcClearDry(), r, l, ReportKind.Scheduled));

        // Obscurations (name themselves in both the visibility and weather rows).
        yield return new("obsc-fog", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], visMiles: 0.25, wx: [Wx(obscuration: WeatherObscuration.Fog)]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("obsc-mist", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], visMiles: 1, wx: [Wx(obscuration: WeatherObscuration.Mist)]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("obsc-haze", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], visMiles: 3, wx: [Wx(obscuration: WeatherObscuration.Haze)]), FcClearDry(), r, l, ReportKind.Scheduled));
        yield return new("obsc-smoke", (r, l) => Report(Obs([Layer(SkyCoverage.Clear, null)], visMiles: 4, wx: [Wx(obscuration: WeatherObscuration.Smoke)]), FcClearDry(), r, l, ReportKind.Scheduled));

        // No observation available → the model-only note.
        yield return new("no-obs", (r, l) => Report(Obs([], wx: [], available: false), FcClearDry(), r, l, ReportKind.Scheduled));

        // Degraded safety send (hazard banner, no band/closing).
        yield return new("degraded", (r, l) => StructuredReportRenderer.RenderDegraded(
            Fc(Blk(12, SkyState.Overcast, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm, severe: true)),
            ObsLowOvercastLightRain(), r, Templates(l), Culture(l), Utc, Now));

        // Welcome (HTML + plain-text fallback).
        yield return new("welcome", (r, l) => StructuredReportRenderer.RenderWelcome(r, Templates(l), Culture(l), "Spring", Utc, ScheduleHours));
        yield return new("welcome-plain", (r, l) => StructuredReportRenderer.WelcomePlainText(r, Templates(l), Culture(l), "Spring", ScheduleHours));
    }

    /// <summary>The full corpus: every scenario in en (imperial) and es (metric), plus the rich scenario in en-metric for unit coverage.</summary>
    public static IReadOnlyList<GoldenCase> All()
    {
        var combos = new (string Suffix, Func<Recipient> R, string Lang)[]
        {
            ("en", Imperial, "en"),
            ("es", Metric, "es"),
        };

        var list = new List<GoldenCase>();
        foreach (var s in Scenarios())
            foreach (var c in combos)
                list.Add(new GoldenCase($"{s.Name}.{c.Suffix}", s.Render(c.R(), c.Lang)));

        // Unit coverage: the rich scenario also in en-metric (same tokens, metric numbers).
        list.Add(new GoldenCase("rich.en-metric", Report(ObsLowOvercastLightRain(), FcStormsThenRain(), Metric(), "en", ReportKind.Scheduled, withChange: true)));
        return list;
    }
}