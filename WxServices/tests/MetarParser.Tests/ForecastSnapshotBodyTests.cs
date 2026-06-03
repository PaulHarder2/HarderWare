using System;
using System.Text.Json;

using MetarParser.Data.Entities;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// Tests for the WX-76 forecast-snapshot body type.  Pins down the canonical
/// JSON form (camelCase properties, snake_case enums), round-trip fidelity,
/// strict enum validation, the precipPhenomenon-null-when-expectation-none
/// convention, and required-field rejection.  These tests describe the
/// contract the GFS-to-provisional builder (WX-77) and the Claude two-pass
/// integration (WX-79) will write against.
/// </summary>
public class ForecastSnapshotBodyTests
{
    private static readonly DateTime AnchorUtc = new(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc);

    private static ForecastSnapshotBlock SampleBlock() => new()
    {
        StartUtc = AnchorUtc,
        SkyState = SkyState.Overcast,
        Obscuration = Obscuration.None,
        TemperatureCelsius = new(8.2, 14.7),
        WindKt = new(5, 12),
        GustOutlook = GustOutlook.Occasional,
        PrecipExpectation = PrecipExpectation.Likely,
        PrecipPhenomenon = PrecipPhenomenon.Rain,
        SevereFlag = false,
        VisibilityExpectation = VisibilityExpectation.Good,
    };

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var body = new ForecastSnapshotBody { Blocks = [SampleBlock()] };

        var roundtripped = ForecastSnapshotBody.Deserialize(body.Serialize());

        // Compare parts explicitly: record equality on the body would fall back
        // to reference equality for the IReadOnlyList<ForecastSnapshotBlock>
        // property (records compare via EqualityComparer<T>.Default, which is
        // reference equality for collection interfaces).  Block equality is
        // proper value equality because ForecastSnapshotBlock is a record
        // with no nested collections.
        Assert.Equal(body.SchemaVersion, roundtripped.SchemaVersion);
        Assert.Equal<ForecastSnapshotBlock>(body.Blocks, roundtripped.Blocks);
    }

    [Fact]
    public void Roundtrip_HandlesMultipleBlocks()
    {
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                SampleBlock() with { StartUtc = AnchorUtc },
                SampleBlock() with
                {
                    StartUtc = AnchorUtc.AddHours(6),
                    SkyState = SkyState.Clear,
                    PrecipExpectation = PrecipExpectation.None,
                    PrecipPhenomenon = null,
                },
                SampleBlock() with
                {
                    StartUtc = AnchorUtc.AddHours(12),
                    PrecipPhenomenon = PrecipPhenomenon.FreezingPrecip,
                    SevereFlag = true,
                },
            ],
        };

        var roundtripped = ForecastSnapshotBody.Deserialize(body.Serialize());

        Assert.Equal(body.SchemaVersion, roundtripped.SchemaVersion);
        Assert.Equal<ForecastSnapshotBlock>(body.Blocks, roundtripped.Blocks);
        Assert.Equal(3, roundtripped.Blocks.Count);
    }

    // ── Canonical JSON form ──────────────────────────────────────────────────

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        string json = new ForecastSnapshotBody { Blocks = [SampleBlock()] }.Serialize();

        Assert.Contains("\"schemaVersion\":", json);
        Assert.Contains("\"blocks\":", json);
        Assert.Contains("\"startUtc\":", json);
        Assert.Contains("\"skyState\":", json);
        Assert.Contains("\"obscuration\":", json);
        Assert.Contains("\"temperatureCelsius\":", json);
        Assert.Contains("\"windKt\":", json);
        Assert.Contains("\"gustOutlook\":", json);
        Assert.Contains("\"precipExpectation\":", json);
        Assert.Contains("\"severeFlag\":", json);
        Assert.Contains("\"visibilityExpectation\":", json);
    }

    [Fact]
    public void Serialize_UsesSnakeCaseLowerForEnums()
    {
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                SampleBlock() with
                {
                    SkyState = SkyState.PartlyCloudy,
                    PrecipPhenomenon = PrecipPhenomenon.FreezingPrecip,
                    GustOutlook = GustOutlook.Frequent,
                },
            ],
        };

        string json = body.Serialize();

        Assert.Contains("\"skyState\":\"partly_cloudy\"", json);
        Assert.Contains("\"precipPhenomenon\":\"freezing_precip\"", json);
        Assert.Contains("\"gustOutlook\":\"frequent\"", json);
    }

    [Fact]
    public void Serialize_OmitsNullPrecipPhenomenon()
    {
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                SampleBlock() with
                {
                    PrecipExpectation = PrecipExpectation.None,
                    PrecipPhenomenon = null,
                },
            ],
        };

        string json = body.Serialize();

        Assert.DoesNotContain("precipPhenomenon", json);
    }

    // ── Strictness ───────────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_RejectsUnknownEnumValue()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "blocks": [
                {
                  "startUtc": "2026-05-26T00:00:00Z",
                  "skyState": "tornado",
                  "obscuration": "none",
                  "temperatureCelsius": { "min": 8.2, "max": 14.7 },
                  "windKt": { "min": 5, "max": 12 },
                  "gustOutlook": "none",
                  "precipExpectation": "none",
                  "severeFlag": false,
                  "visibilityExpectation": "good"
                }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => ForecastSnapshotBody.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsMissingRequiredField()
    {
        // "skyState" omitted from the only block.
        const string json = """
            {
              "schemaVersion": 1,
              "blocks": [
                {
                  "startUtc": "2026-05-26T00:00:00Z",
                  "obscuration": "none",
                  "temperatureCelsius": { "min": 8.2, "max": 14.7 },
                  "windKt": { "min": 5, "max": 12 },
                  "gustOutlook": "none",
                  "precipExpectation": "none",
                  "severeFlag": false,
                  "visibilityExpectation": "good"
                }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => ForecastSnapshotBody.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsPrecipNoneWithPhenomenon()
    {
        // precipExpectation = "none" but precipPhenomenon is non-null — violates the invariant.
        const string json = """
            {
              "schemaVersion": 1,
              "blocks": [
                {
                  "startUtc": "2026-05-26T00:00:00Z",
                  "skyState": "overcast",
                  "obscuration": "none",
                  "temperatureCelsius": { "min": 8.2, "max": 14.7 },
                  "windKt": { "min": 5, "max": 12 },
                  "gustOutlook": "none",
                  "precipExpectation": "none",
                  "precipPhenomenon": "rain",
                  "severeFlag": false,
                  "visibilityExpectation": "good"
                }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => ForecastSnapshotBody.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsNonNonePrecipMissingPhenomenon()
    {
        // precipExpectation = "likely" but precipPhenomenon is omitted (defaults to null) — violates the invariant.
        const string json = """
            {
              "schemaVersion": 1,
              "blocks": [
                {
                  "startUtc": "2026-05-26T00:00:00Z",
                  "skyState": "overcast",
                  "obscuration": "none",
                  "temperatureCelsius": { "min": 8.2, "max": 14.7 },
                  "windKt": { "min": 5, "max": 12 },
                  "gustOutlook": "occasional",
                  "precipExpectation": "likely",
                  "severeFlag": false,
                  "visibilityExpectation": "good"
                }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => ForecastSnapshotBody.Deserialize(json));
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_BodyHasCurrentSchemaVersionAndEmptyBlocks()
    {
        var body = new ForecastSnapshotBody();

        Assert.Equal(ForecastSnapshotBody.SchemaVersionCurrent, body.SchemaVersion);
        Assert.Empty(body.Blocks);
    }

    // ── Material equality (WX-108 redundancy backstop) ─────────────────────────

    private static ForecastSnapshotBody Body(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    [Fact]
    public void MateriallyEquals_IdenticalBodies_True()
    {
        Assert.True(Body(SampleBlock()).MateriallyEquals(Body(SampleBlock())));
    }

    [Fact]
    public void MateriallyEquals_SmallTempAndWindDrift_True()
    {
        // +1.0/+1.3 °C and +2/+4 kt — inside the 2 °C / 5 kt tolerances.
        var drifted = SampleBlock() with
        {
            TemperatureCelsius = new(9.2, 16.0),
            WindKt = new(7, 16),
        };
        Assert.True(Body(SampleBlock()).MateriallyEquals(Body(drifted)));
    }

    [Fact]
    public void MateriallyEquals_WindDriftWithinSameBand_True()
    {
        // WX-110: 3→14 and 9→16 kt both exceed the 5 kt tolerance but stay in the
        // same ≤17 kt impact band — a breeze a reader would shrug at, not news.
        var prior = SampleBlock() with { WindKt = new(3, 9) };
        var drifted = SampleBlock() with { WindKt = new(14, 16) };
        Assert.True(Body(prior).MateriallyEquals(Body(drifted)));
    }

    [Fact]
    public void MateriallyEquals_WindCrossesIntoHigherBand_False()
    {
        // A genuine shift into a windier regime (15 kt, band 0 → 25 kt, band 1) is
        // beyond both the tolerance and the band, so it is still treated as news.
        var prior = SampleBlock() with { WindKt = new(10, 15) };
        var windier = SampleBlock() with { WindKt = new(10, 25) };
        Assert.False(Body(prior).MateriallyEquals(Body(windier)));
    }

    [Fact]
    public void MateriallyEquals_SmallDriftAcrossBandBoundary_True()
    {
        // 16→18 kt crosses the 17 kt boundary but drifts only 2 kt; the absolute
        // tolerance keeps it equal so a band edge does not become a hair-trigger.
        var prior = SampleBlock() with { WindKt = new(5, 16) };
        var nudged = SampleBlock() with { WindKt = new(5, 18) };
        Assert.True(Body(prior).MateriallyEquals(Body(nudged)));
    }

    [Fact]
    public void MateriallyEquals_LargeTempSwing_False()
    {
        var hotter = SampleBlock() with { TemperatureCelsius = new(8.2, 20.0) }; // +5.3 °C max
        Assert.False(Body(SampleBlock()).MateriallyEquals(Body(hotter)));
    }

    [Fact]
    public void MateriallyEquals_PrecipTierChange_False()
    {
        var wetter = SampleBlock() with { PrecipExpectation = PrecipExpectation.Certain };
        Assert.False(Body(SampleBlock()).MateriallyEquals(Body(wetter)));
    }

    [Fact]
    public void MateriallyEquals_SevereFlagFlip_FalseButIgnoringSevereTrue()
    {
        var severe = SampleBlock() with { SevereFlag = true };
        Assert.False(Body(SampleBlock()).MateriallyEquals(Body(severe)));
        Assert.True(Body(SampleBlock()).MateriallyEqualsIgnoringSevere(Body(severe)));
    }

    [Fact]
    public void MateriallyEquals_NonOverlappingHorizonEdge_ComparesOnlySharedBlocks()
    {
        var b0 = SampleBlock();
        var b1 = SampleBlock() with { StartUtc = AnchorUtc.AddHours(6) };
        var b2 = SampleBlock() with { StartUtc = AnchorUtc.AddHours(12) };
        // prior covers {b0,b1}; new covers {b1,b2}. Overlap is b1 (equal); the
        // rolled edge (b0 dropped, b2 added) is the passage of time, not news.
        Assert.True(Body(b0, b1).MateriallyEquals(Body(b1, b2)));
    }

    [Fact]
    public void MateriallyEquals_BothEmpty_True()
    {
        Assert.True(new ForecastSnapshotBody().MateriallyEquals(new ForecastSnapshotBody()));
    }

    [Fact]
    public void MateriallyEquals_EmptyVsPopulated_False()
    {
        Assert.False(new ForecastSnapshotBody().MateriallyEquals(Body(SampleBlock())));
    }
}