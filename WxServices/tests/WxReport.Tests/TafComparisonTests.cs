using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-506 v1.61.8: the database half of the "same forecast in substance" test for a new TAF.
// WxInterpreter.LoadTafAsync finds the TAF Claude weighed at a rejection by station and
// issuance, and ReportWorker.TafSameInSubstanceAsync compares it with the current TAF.
// Anything it cannot compare must read as different, so Claude is asked.
public sealed class TafComparisonTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<WeatherDataContext> _db;

    // Spring, TX on 2026-10-01: KIAH amended through the afternoon.
    private static readonly DateTime Now = new(2026, 10, 1, 15, 35, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = new(2026, 10, 1, 14, 43, 0);   // as read back from the database: no kind
    private static readonly DateTime Reissued = new(2026, 10, 1, 15, 27, 0);
    private static readonly DateTime Changed = new(2026, 10, 1, 15, 59, 0);
    private static readonly DateTime ValidTo = new(2026, 10, 2, 18, 0, 0);

    public TafComparisonTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(_conn).Options;
        using var ctx = new WeatherDataContext(_db);
        ctx.Database.ExecuteSqlRaw(ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT"));

        // The earlier TAF and a reissue saying the same thing: dry, with a TEMPO shower in the afternoon.
        Seed(ctx, Earlier, tempoDescriptor: "SH");
        Seed(ctx, Reissued, tempoDescriptor: "SH");
        // An amendment upgrading the TEMPO showers to thunderstorms.
        Seed(ctx, Changed, tempoDescriptor: "TS");
        ctx.SaveChanges();
    }

    public void Dispose() => _conn.Dispose();

    private static void Seed(WeatherDataContext ctx, DateTime issued, string tempoDescriptor) =>
        ctx.Tafs.Add(new TafRecord
        {
            ReportType = "TAF",
            StationIcao = "KIAH",
            IssuanceUtc = issued,
            ValidFromUtc = issued,
            ValidToUtc = ValidTo,
            ReceivedUtc = issued.AddMinutes(5),
            RawReport = "TAF KIAH",
            ChangePeriods =
            [
                new TafChangePeriodRecord { ChangeType = "BASE", ValidFromUtc = issued, ValidToUtc = ValidTo, WindSpeed = 12, WindUnit = "KT", SortOrder = 0 },
                new TafChangePeriodRecord
                {
                    ChangeType = "TEMPO", ValidFromUtc = new DateTime(2026, 10, 1, 18, 0, 0), ValidToUtc = new DateTime(2026, 10, 1, 21, 0, 0),
                    SortOrder = 1,
                    WeatherPhenomena = [new TafChangePeriodWeather { Intensity = "-", Descriptor = tempoDescriptor, Precipitation = "RA", SortOrder = 0 }],
                },
            ],
        });

    // The gate's blocks for a Central-time locality: local day-parts, 05/11/17/23Z.
    private static ForecastSnapshotBody Blocks()
    {
        var start = new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc);
        return new ForecastSnapshotBody
        {
            Blocks = Enumerable.Range(0, 8).Select(i => new ForecastSnapshotBlock
            {
                StartUtc = start.AddHours(6 * i),
                SkyState = SkyState.Clear,
                Obscuration = Obscuration.None,
                TemperatureCelsius = new(20, 30),
                WindKt = new(0, 10),
                PrecipExpectation = PrecipExpectation.None,
                SevereFlag = false,
            }).ToList(),
        };
    }

    private async Task<WeatherSnapshot> CurrentAsync(DateTime issued)
    {
        await using var ctx = new WeatherDataContext(_db);
        var taf = (await WxInterpreter.LoadTafAsync(ctx, "KIAH", issued))!.Value;
        return new WeatherSnapshot
        {
            TafStationIcao = "KIAH",
            TafIssuanceUtc = issued,
            TafValidToUtc = taf.ValidToUtc,
            ForecastPeriods = taf.Periods.ToList(),
        };
    }

    private async Task<TafComparison> OutcomeAsync(WeatherSnapshot current, string recordedTaf)
    {
        await using var ctx = new WeatherDataContext(_db);
        return (await ReportWorker.CompareTafsAsync(ctx, current, recordedTaf, Blocks(), Now, CancellationToken.None)).Outcome;
    }

    private async Task<bool> SameAsync(WeatherSnapshot current, string recordedTaf)
    {
        await using var ctx = new WeatherDataContext(_db);
        return await ReportWorker.TafSameInSubstanceAsync(ctx, current, recordedTaf, Blocks(), Now, "test", CancellationToken.None);
    }

    private static WeatherSnapshot NoTaf() => new() { TafStationIcao = null, TafIssuanceUtc = null, TafValidToUtc = null };

    [Fact]
    public async Task LoadTaf_FindsTheTafByStationAndIssuance()
    {
        await using var ctx = new WeatherDataContext(_db);
        var taf = await WxInterpreter.LoadTafAsync(ctx, "KIAH", Changed);
        Assert.NotNull(taf);
        Assert.Equal(2, taf.Value.Periods.Count);
        Assert.Equal(WeatherDescriptor.Thunderstorm, taf.Value.Periods[1].WeatherPhenomena[0].Descriptor);
        Assert.Null(await WxInterpreter.LoadTafAsync(ctx, "KIAH", Changed.AddMinutes(1)));
        Assert.Null(await WxInterpreter.LoadTafAsync(ctx, "KAUS", Changed));
    }

    [Fact]
    public async Task AReissueSayingTheSameThing_IsSame()
    {
        Assert.Equal(TafComparison.Same, await OutcomeAsync(await CurrentAsync(Reissued), Earlier.ToString("O")));
        Assert.True(await SameAsync(await CurrentAsync(Reissued), Earlier.ToString("O")));
    }

    [Fact]
    public async Task TheRecordedIssuanceMatches_WithOrWithoutAUtcMarker() =>
        Assert.Equal(TafComparison.Same,
            await OutcomeAsync(await CurrentAsync(Reissued), DateTime.SpecifyKind(Earlier, DateTimeKind.Utc).ToString("O")));

    [Fact]
    public async Task ShowersBecomingThunderstorms_IsDifferent()
    {
        Assert.Equal(TafComparison.Different, await OutcomeAsync(await CurrentAsync(Changed), Earlier.ToString("O")));
        Assert.False(await SameAsync(await CurrentAsync(Changed), Earlier.ToString("O")));
    }

    [Fact]
    public async Task AnEarlierTafNoLongerStored_Fails() =>
        Assert.Equal(TafComparison.Failed, await OutcomeAsync(await CurrentAsync(Reissued), Earlier.AddMinutes(-30).ToString("O")));

    [Fact]
    public async Task AnUnreadableRecordedIssuance_Fails()
    {
        Assert.Equal(TafComparison.Failed, await OutcomeAsync(await CurrentAsync(Reissued), "not a time"));
        Assert.False(await SameAsync(await CurrentAsync(Reissued), "not a time"));
    }

    [Fact]
    public async Task NoTafAtTheRejection_CameOrWent() =>
        // A part-time TAF station: no TAF when Claude answered, one now. A real difference, not a failure.
        Assert.Equal(TafComparison.CameOrWent, await OutcomeAsync(await CurrentAsync(Reissued), "none"));

    [Fact]
    public async Task NoTafNow_CameOrWent()
    {
        // The TAF expired since the rejection. A real difference, not a failure.
        Assert.Equal(TafComparison.CameOrWent, await OutcomeAsync(NoTaf(), Earlier.ToString("O")));
        Assert.False(await SameAsync(NoTaf(), Earlier.ToString("O")));
    }

    // The production watch greps these lines (WX-506.md step 6c): only a real failure may be a
    // warning, and a TAF that came or went must read as a difference.
    [Theory]
    [InlineData(nameof(TafComparison.Same), false, "WX-506 TAF comparison same")]
    [InlineData(nameof(TafComparison.Different), false, "WX-506 TAF comparison different")]
    [InlineData(nameof(TafComparison.CameOrWent), false, "WX-506 TAF comparison different")]
    [InlineData(nameof(TafComparison.Failed), true, "WX-506 could not compare the recorded TAF")]
    public void EachOutcome_LogsTheLineTheWatchCounts(string outcome, bool warn, string prefix)
    {
        var (isWarn, line) = ReportWorker.TafComparisonLogLine(Enum.Parse<TafComparison>(outcome), "detail");
        Assert.Equal(warn, isWarn);
        Assert.StartsWith(prefix, line);
    }
}