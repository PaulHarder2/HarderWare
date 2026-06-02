using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

namespace WxReport.Tests.Scenarios;

// Fixtures for the 2026-04-21 KDWH double-send scenario (WX-82, WX-47 epic).
//
// On 2026-04-21 WxReport sent a 4:53 AM scheduled report predicting a rainy
// Tuesday, then an 8:53 AM *unscheduled* update saying "light rain has moved in,
// replacing the previously dry conditions" — both clauses wrong: rain was already
// predicted, and the prior report never said "dry". The new forecast-relative
// path must NOT emit that unscheduled update: at 8:53 the observed light rain
// matches the already-committed rainy forecast, so Claude's gate returns skip_send.
//
// Source: the two original emails under C:\HarderWare\Email\ (Spring, TX). Values
// are faithful to those emails; the recipient is a generic test recipient (no PII).
//
// These fixtures feed two paths:
//   1. The deterministic CI replay (KdwhScenarioReplayTests) — drives
//      ForecastReconciler.ReconcileAsync with a *recorded* Claude response.
//   2. The opt-in recorder (KdwhScenarioReplayRecorder) — runs the same inputs
//      against the real Claude API once to capture those recorded responses.
internal static class Kdwh20260421Fixture
{
    // ── scenario constants ───────────────────────────────────────────────────

    internal const string StationIcao = "KDWH";
    internal const string TafStationIcao = "KIAH";
    internal const string LocalityName = "Spring, TX";
    internal const string RecipientName = "Test Recipient";

    // GFS run shared by both the 4:53 and 8:53 cycles (email footer: GFS 0600Z).
    internal static readonly DateTime GfsModelRunUtc = new(2026, 4, 21, 6, 0, 0, DateTimeKind.Utc);

    // 4:53 AM local / 09:53 UTC scheduled observation (the prior committed cycle).
    internal static readonly DateTime Obs0453Utc = new(2026, 4, 21, 9, 53, 0, DateTimeKind.Utc);

    // 8:53 AM local / 13:53 UTC observation (the replay cycle — light rain).
    internal static readonly DateTime Obs0853Utc = new(2026, 4, 21, 13, 53, 0, DateTimeKind.Utc);

    // TAF issuance for the 8:53 cycle. The KIAH TAF issues ~6x/day; this run
    // post-dates the 06Z GFS, so the TAF prevails where it diverges (recon step 1).
    internal static readonly DateTime Taf0853IssuanceUtc = new(2026, 4, 21, 11, 38, 0, DateTimeKind.Utc);
    internal static readonly DateTime Taf0853ValidToUtc = new(2026, 4, 22, 18, 0, 0, DateTimeKind.Utc);

    // TAF issuance for the 4:53 cycle (an earlier KIAH run).
    internal static readonly DateTime Taf0453IssuanceUtc = new(2026, 4, 21, 5, 38, 0, DateTimeKind.Utc);

    internal static readonly TimeZoneInfo CentralTz =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

    internal const int ScheduledHour = 4;

    // ── 8:53 observation snapshot (the replay cycle) ─────────────────────────

    // KDWH 8:53 AM: wind 100° 7 kt, vis 10 SM, OVC 4600 ft, Light Rain, 63°F (17.2°C),
    // altimeter 30.21 inHg. TAF: VCSH base, FM light showers rain, PROB30 afternoon
    // thunderstorm. GFS 06Z: rainy Tue (CAPE 13, precip ~1.1 mm/hr) warming midweek.
    internal static WeatherSnapshot BuildSnapshot0853() => new()
    {
        ObservationAvailable = true,
        StationIcao = StationIcao,
        StationMunicipality = "Houston",
        StationName = "David Wayne Hooks Memorial Airport",
        LocalityName = LocalityName,
        ObservationTimeUtc = Obs0853Utc,
        IsAutomated = true,

        WindDirectionDeg = 100,
        WindSpeedKt = 7,

        VisibilityStatuteMiles = 10,

        SkyLayers = new[]
        {
            new SkyLayer { Coverage = SkyCoverage.Overcast, HeightFeet = 4600 },
        },

        WeatherPhenomena = new[]
        {
            new SnapshotWeather { Intensity = WeatherIntensity.Light, Precipitation = new[] { PrecipitationType.Rain } },
        },

        TemperatureCelsius = 17.2,
        TemperatureFahrenheit = 63,
        DewPointCelsius = 13.0,
        AltimeterInHg = 30.21,

        TafStationIcao = TafStationIcao,
        TafIssuanceUtc = Taf0853IssuanceUtc,
        TafValidToUtc = Taf0853ValidToUtc,
        ForecastPeriods = Build0853TafPeriods(),

        GfsForecast = BuildGfsForecast(),
    };

    // A genuinely off-forecast mutation of the 8:53 cycle: a severe thunderstorm
    // with damaging-gust winds that the committed rainy-but-non-severe forecast
    // did NOT promise. Used as the positive control — this SHOULD produce a send.
    internal static WeatherSnapshot BuildSnapshot0853StormMutation()
    {
        // WeatherSnapshot is init-only (no `with`), so the unchanged fields are
        // re-listed explicitly. Keep the non-storm fields in sync with
        // BuildSnapshot0853() if that base observation changes.
        var snap = BuildSnapshot0853();
        return new WeatherSnapshot
        {
            ObservationAvailable = true,
            StationIcao = snap.StationIcao,
            StationMunicipality = snap.StationMunicipality,
            StationName = snap.StationName,
            LocalityName = snap.LocalityName,
            ObservationTimeUtc = snap.ObservationTimeUtc,
            IsAutomated = snap.IsAutomated,

            WindDirectionDeg = 230,
            WindSpeedKt = 22,
            WindGustKt = 41, // damaging gusts, > 34 kt

            VisibilityStatuteMiles = 1.5,

            SkyLayers = new[]
            {
                new SkyLayer { Coverage = SkyCoverage.Broken, HeightFeet = 2500, CloudType = CloudType.Cumulonimbus },
                new SkyLayer { Coverage = SkyCoverage.Overcast, HeightFeet = 6000 },
            },

            WeatherPhenomena = new[]
            {
                new SnapshotWeather
                {
                    Intensity = WeatherIntensity.Heavy,
                    Descriptor = WeatherDescriptor.Thunderstorm,
                    Precipitation = new[] { PrecipitationType.Rain, PrecipitationType.Hail },
                },
            },

            TemperatureCelsius = 19.0,
            TemperatureFahrenheit = 66,
            DewPointCelsius = 17.0,
            AltimeterInHg = 29.92,

            TafStationIcao = snap.TafStationIcao,
            TafIssuanceUtc = snap.TafIssuanceUtc,
            TafValidToUtc = snap.TafValidToUtc,
            ForecastPeriods = snap.ForecastPeriods,

            GfsForecast = snap.GfsForecast,
        };
    }

    private static IReadOnlyList<ForecastPeriod> Build0853TafPeriods() => new[]
    {
        new ForecastPeriod
        {
            ChangeType = ForecastChangeType.Base,
            ValidFromUtc = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc),
            ValidToUtc = Taf0853ValidToUtc,
            WindDirectionDeg = 110, WindSpeedKt = 12, VisibilityStatuteMiles = 6,
            SkyLayers = new[]
            {
                new SkyLayer { Coverage = SkyCoverage.Scattered, HeightFeet = 3000 },
                new SkyLayer { Coverage = SkyCoverage.Broken, HeightFeet = 5000 },
                new SkyLayer { Coverage = SkyCoverage.Overcast, HeightFeet = 10000 },
            },
            WeatherPhenomena = new[]
            {
                new SnapshotWeather { Intensity = WeatherIntensity.InTheVicinity, Descriptor = WeatherDescriptor.Showers },
            },
        },
        new ForecastPeriod
        {
            ChangeType = ForecastChangeType.From,
            ValidFromUtc = new DateTime(2026, 4, 21, 15, 0, 0, DateTimeKind.Utc),
            WindDirectionDeg = 100, WindSpeedKt = 11, VisibilityStatuteMiles = 6,
            SkyLayers = new[]
            {
                new SkyLayer { Coverage = SkyCoverage.Scattered, HeightFeet = 2500 },
                new SkyLayer { Coverage = SkyCoverage.Overcast, HeightFeet = 3500 },
            },
            WeatherPhenomena = new[]
            {
                new SnapshotWeather { Intensity = WeatherIntensity.Light, Descriptor = WeatherDescriptor.Showers, Precipitation = new[] { PrecipitationType.Rain } },
            },
        },
        new ForecastPeriod
        {
            ChangeType = ForecastChangeType.Probability30,
            ValidFromUtc = new DateTime(2026, 4, 21, 19, 0, 0, DateTimeKind.Utc),
            ValidToUtc = new DateTime(2026, 4, 21, 23, 0, 0, DateTimeKind.Utc),
            VisibilityStatuteMiles = 4,
            SkyLayers = new[] { new SkyLayer { Coverage = SkyCoverage.Overcast, HeightFeet = 2500 } },
            WeatherPhenomena = new[]
            {
                new SnapshotWeather { Intensity = WeatherIntensity.Moderate, Descriptor = WeatherDescriptor.Thunderstorm, Precipitation = new[] { PrecipitationType.Rain } },
            },
        },
    };

    private static GfsForecast BuildGfsForecast() => new()
    {
        ModelRunUtc = GfsModelRunUtc,
        Days = new[]
        {
            new GfsDailyForecast { Date = new DateOnly(2026, 4, 21), HighTempF = 68, HighTempC = 20.0f, LowTempF = 60, LowTempC = 15.6f, MaxWindSpeedKt = 10, DominantWindDirDeg = 90, MaxCloudCoverPct = 100, MaxCapeJKg = 13, MaxPrecipRateMmHr = 1.1f },
            new GfsDailyForecast { Date = new DateOnly(2026, 4, 22), HighTempF = 84, HighTempC = 28.9f, LowTempF = 63, LowTempC = 17.2f, MaxWindSpeedKt = 9, DominantWindDirDeg = 120, MaxCloudCoverPct = 100, MaxCapeJKg = 1042, MaxPrecipRateMmHr = 0.3f },
            new GfsDailyForecast { Date = new DateOnly(2026, 4, 23), HighTempF = 82, HighTempC = 27.8f, LowTempF = 67, LowTempC = 19.4f, MaxWindSpeedKt = 10, DominantWindDirDeg = 180, MaxCloudCoverPct = 100, MaxCapeJKg = 1217, MaxPrecipRateMmHr = 0.2f },
        },
    };

    // ── provisional snapshot (GFS-derived, slot B) ───────────────────────────

    // Rainy Tuesday afternoon/evening from the 06Z GFS: precip likely, rain, not
    // severe. Two 6-hour blocks spanning the 8:53 cycle's near horizon.
    internal static ForecastSnapshotBody BuildProvisionalBody() => new()
    {
        SchemaVersion = 1,
        Blocks = new[]
        {
            new ForecastSnapshotBlock
            {
                StartUtc = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc),
                SkyState = SkyState.Overcast, Obscuration = Obscuration.None,
                TemperatureCelsius = new MinMax<double>(16.0, 20.0),
                WindKt = new MinMax<int>(7, 12), GustOutlook = GustOutlook.None,
                PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain,
                SevereFlag = false, VisibilityExpectation = VisibilityExpectation.Reduced,
            },
            new ForecastSnapshotBlock
            {
                StartUtc = new DateTime(2026, 4, 21, 18, 0, 0, DateTimeKind.Utc),
                SkyState = SkyState.Overcast, Obscuration = Obscuration.None,
                TemperatureCelsius = new MinMax<double>(17.0, 20.0),
                WindKt = new MinMax<int>(6, 12), GustOutlook = GustOutlook.Occasional,
                // Tuesday CAPE was 13 J/kg (low) — plain rain, not a thunderstorm.
                // The convective threat was Wednesday onward (CAPE 1042+).
                PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain,
                SevereFlag = false, VisibilityExpectation = VisibilityExpectation.Reduced,
            },
        },
    };

    // ── prior committed snapshot (what the 4:53 report told the recipient) ────

    // The 4:53 scheduled send committed a rainy Tuesday — "Overcast with steady
    // rain showers throughout the day." The 8:53 observed light rain matches this,
    // which is exactly why the unscheduled update is NOT news.
    internal static ForecastSnapshot BuildPriorCommittedSnapshot() => new()
    {
        StationIcao = StationIcao,
        GeneratedAtUtc = Obs0453Utc,
        SchemaVersion = 1,
        Body = BuildPriorBody().Serialize(),
    };

    private static ForecastSnapshotBody BuildPriorBody() => new()
    {
        SchemaVersion = 1,
        Blocks = new[]
        {
            new ForecastSnapshotBlock
            {
                StartUtc = new DateTime(2026, 4, 21, 6, 0, 0, DateTimeKind.Utc),
                SkyState = SkyState.Overcast, Obscuration = Obscuration.None,
                TemperatureCelsius = new MinMax<double>(15.6, 18.0),
                WindKt = new MinMax<int>(5, 8), GustOutlook = GustOutlook.None,
                PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain,
                SevereFlag = false, VisibilityExpectation = VisibilityExpectation.Reduced,
            },
            new ForecastSnapshotBlock
            {
                StartUtc = new DateTime(2026, 4, 21, 12, 0, 0, DateTimeKind.Utc),
                SkyState = SkyState.Overcast, Obscuration = Obscuration.None,
                TemperatureCelsius = new MinMax<double>(16.0, 20.0),
                WindKt = new MinMax<int>(7, 12), GustOutlook = GustOutlook.None,
                PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain,
                SevereFlag = false, VisibilityExpectation = VisibilityExpectation.Reduced,
            },
            new ForecastSnapshotBlock
            {
                // The 4:53 report committed rain "throughout the day", so the
                // committed snapshot covers the evening too — the 8:53 observed
                // light rain confirms it rather than adding anything new.
                StartUtc = new DateTime(2026, 4, 21, 18, 0, 0, DateTimeKind.Utc),
                SkyState = SkyState.Overcast, Obscuration = Obscuration.None,
                TemperatureCelsius = new MinMax<double>(17.0, 20.0),
                WindKt = new MinMax<int>(6, 12), GustOutlook = GustOutlook.Occasional,
                PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain,
                SevereFlag = false, VisibilityExpectation = VisibilityExpectation.Reduced,
            },
        },
    };

    // ── WX-80 pre-filter identities ──────────────────────────────────────────

    // The serialized InputIdentity stored at the 4:53 Claude call (the baseline
    // the 8:53 cycle's pre-filter compares against).
    internal static string PriorInputHash() => new InputIdentity(
        Metar: $"{StationIcao}@{Obs0453Utc:O}",
        Taf: Taf0453IssuanceUtc.ToString("O"),
        Gfs: GfsModelRunUtc.ToString("O")).Serialize();

    // The 8:53 cycle's input identity (a new METAR has arrived).
    internal static InputIdentity Identity0853() => InputIdentity.From(BuildSnapshot0853());

    // ── reconciliation driver (shared by the replay tests and the recorder) ──

    // Drives ForecastReconciler.ReconcileAsync with this scenario's fixed
    // arguments for the given observation snapshot and Claude client. Owning the
    // long argument list in one place keeps the replay tests and the recorder in
    // lockstep when ReconcileAsync's signature changes — a drift the recorder
    // (opt-in, normally a no-op) would otherwise only reveal months later.
    internal static Task<ReconcileResult> Reconcile(ClaudeClient claude, WeatherSnapshot snapshot, CancellationToken ct = default)
    {
        var reconciler = new ForecastReconciler(claude);
        return reconciler.ReconcileAsync(
            snapshot: snapshot,
            provisional: BuildProvisionalBody(),
            gfsModelRunUtc: GfsModelRunUtc,
            tafIssuanceUtc: Taf0853IssuanceUtc,
            tafValidToUtc: Taf0853ValidToUtc,
            prior: BuildPriorCommittedSnapshot(),
            language: "English",
            recipientName: RecipientName,
            tz: CentralTz,
            isFirstReport: false,
            scheduledHour: ScheduledHour,
            units: null,
            changeSeverity: ChangeSeverity.Update,
            previousMetarIcao: null,
            allowSkip: true,
            // The 8:53 cycle is the canonical observation-only advance: a new METAR
            // over the 4:53 prior, no fresh TAF/GFS — exactly what WX-108's
            // anti-reversal context describes.
            changedSinceLastSend: new[] { TriggerSource.Metar },
            ct: ct);
    }
}