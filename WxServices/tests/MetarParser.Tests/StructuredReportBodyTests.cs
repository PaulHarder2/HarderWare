using System;
using System.Collections.Generic;
using System.Text.Json;

using MetarParser.Data.Entities;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// Tests for the WX-128 structured-report body type.  Pins down the canonical
/// JSON form (camelCase properties, snake_case enums, language-keyed
/// narrative), round-trip fidelity, and the token grammar (quantity and time
/// tokens).  Since WX-189 the change set is computed deterministically, not
/// authored by Claude, so {chN} anchors are forbidden in either prose section
/// (the old anchor↔change correspondence is retired).  These tests describe the
/// contract the Claude reconciliation writes against and the WX-129
/// deterministic renderer will consume.
/// </summary>
public class StructuredReportBodyTests
{
    private static readonly DateTime AnchorUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc);

    private const string LongProse =
        "Showers are likely overnight into Friday morning, with thunderstorms possible "
        + "in the afternoon. The unsettled, humid pattern continues through the weekend "
        + "before drier air arrives early next week and skies gradually clear.";

    private static ReportChange SampleChange(string token = "ch1") => new()
    {
        Tier = ChangeTier.Plans,
        Phenomenon = ChangePhenomenon.Thunderstorm,
        Direction = ChangeDirection.Appearing,
        Window = new ChangeWindow(AnchorUtc, AnchorUtc.AddHours(6)),
        Quantities = [new ReportQuantity { Kind = QuantityKind.Gust, Value = 30 }],
        SummaryToken = token,
    };

    private static NarrativeSections SampleSections(string? changeSummary) => new()
    {
        ChangeSummary = changeSummary,
        Closing = "A stormy stretch ahead — keep an eye on the afternoon sky.",
    };

    private static StructuredReportBody SampleBody() => new()
    {
        Changes = [SampleChange()],
        Narrative = new Dictionary<string, NarrativeSections>
        {
            ["en"] = SampleSections("Thunderstorms are now expected after {q:time:2026-06-08T21:00:00Z}, with gusts to {q:gust:30} possible."),
            ["es"] = SampleSections("Ahora se esperan tormentas después de las {q:time:2026-06-08T21:00:00Z}, con ráfagas de hasta {q:gust:30}."),
        },
    };

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var body = SampleBody();

        var roundtripped = StructuredReportBody.Deserialize(body.Serialize());

        // Compare parts explicitly: record equality on the body would fall back
        // to reference equality for the collection-typed properties.
        Assert.Equal(body.SchemaVersion, roundtripped.SchemaVersion);
        Assert.Equal(StructuredReportBody.SchemaVersionCurrent, roundtripped.SchemaVersion);
        Assert.Single(roundtripped.Changes);
        Assert.Equal(body.Changes[0].Tier, roundtripped.Changes[0].Tier);
        Assert.Equal(body.Changes[0].Window, roundtripped.Changes[0].Window);
        Assert.Equal<ReportQuantity>(body.Changes[0].Quantities, roundtripped.Changes[0].Quantities);
        Assert.Equal(new[] { "en", "es" }, roundtripped.Narrative.Keys.Order());
        Assert.Equal(body.Narrative["es"].ChangeSummary, roundtripped.Narrative["es"].ChangeSummary);
    }

    [Fact]
    public void Serialize_UsesSnakeCaseEnums()
    {
        var json = (SampleBody() with
        {
            Changes =
            [
                SampleChange() with
                {
                    Phenomenon = ChangePhenomenon.FreezingPrecip,
                    Direction = ChangeDirection.Strengthening,
                    Quantities = [new ReportQuantity { Kind = QuantityKind.PrecipMm, Value = 12 }],
                },
            ],
        }).Serialize();

        Assert.Contains("\"freezing_precip\"", json);
        Assert.Contains("\"strengthening\"", json);
        Assert.Contains("\"precip_mm\"", json);
    }

    [Fact]
    public void VersionConsts_AreInLockstep()
    {
        // WX-128 design decision: the two body types travel in the same
        // tool_use envelope and version together.
        Assert.Equal(ForecastSnapshotBody.SchemaVersionCurrent, StructuredReportBody.SchemaVersionCurrent);
    }

    // ── Narrative shape ──────────────────────────────────────────────────────

    [Fact]
    public void EmptyNarrative_IsRejected()
    {
        var body = SampleBody() with { Narrative = new Dictionary<string, NarrativeSections>(), Changes = [] };

        Assert.Throws<JsonException>(() => body.Serialize());
    }

    [Theory]
    [InlineData("english")]
    [InlineData("EN")]
    [InlineData("e")]
    public void NonIsoLanguageKey_IsRejected(string key)
    {
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections> { [key] = SampleSections(null) },
        };

        Assert.Throws<JsonException>(() => body.Serialize());
    }

    [Fact]
    public void BlankRequiredSection_IsRejected()
    {
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = "   " },
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("closing", ex.Message);
    }

    // ── Token grammar ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{q:temp:abc}")]          // unparsable value
    [InlineData("{q:fahrenheit:90}")]     // unknown kind (units are canonical, never named)
    [InlineData("{q:time:tomorrow}")]     // unparsable timestamp
    [InlineData("{temp:30}")]             // not a recognized token form
    [InlineData("{q:wind:}")]             // empty value
    public void MalformedToken_IsRejected(string token)
    {
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = $"Winds increasing to {token} later today." },
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("token", ex.Message);
    }

    [Theory]
    [InlineData("gusts to {q:gust:41} kt")]
    [InlineData("winds near {q:wind:22} knots")]
    [InlineData("highs of {q:temp:33.5} degrees")]
    [InlineData("ráfagas de hasta {q:gust:30} nudos")]
    [InlineData("pressure {q:pressure:1013.2} hPa rising")]
    [InlineData("about {q:precip_mm:12} mm of rain")]
    public void LiteralUnitAfterToken_IsRejected(string prose)
    {
        // The renderer appends the unit at substitution time, so "{q:gust:41} kt"
        // would render "47 mph kt" for a US recipient. Claude's first live call
        // against the v3 schema did exactly this (WX-128 re-record finding) —
        // enforced deterministically, not just prompted.
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = $"{LongProse} Expect {prose} later today." },
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("literal unit", ex.Message);
    }

    [Theory]
    [InlineData("by {q:time:2026-06-08T21:00:00Z} the morning commute clears")]
    [InlineData("highs near {q:temp:33.5} today")]
    [InlineData("gusts to {q:gust:30}, possibly stronger")]
    public void TokenWithoutTrailingUnit_IsAccepted(string prose)
    {
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = $"{LongProse} Expect {prose}." },
            },
        };

        var roundtripped = StructuredReportBody.Deserialize(body.Serialize());

        Assert.Contains("Expect", roundtripped.Narrative["en"].Closing);
    }

    [Fact]
    public void AllCanonicalQuantityKinds_AreAccepted()
    {
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with
                {
                    Closing = "Temp {q:temp:-3.5}, wind {q:wind:22}, gust {q:gust:35}, "
                        + "pressure {q:pressure:1013.2}, rain {q:precip_mm:12}, by {q:time:2026-06-08T21:00:00Z}.",
                },
            },
        };

        var roundtripped = StructuredReportBody.Deserialize(body.Serialize());

        Assert.Contains("{q:temp:-3.5}", roundtripped.Narrative["en"].Closing);
    }

    // ── Change-anchor cross-references ───────────────────────────────────────

    [Fact]
    public void AnchorInChangeSummary_IsRejected()
    {
        // WX-189: the change set is computed deterministically, not narrated by
        // anchors, so a {chN} anchor in the changeSummary is now rejected outright
        // (the old "every change narrated / no dangling anchor" correspondence is
        // retired with the anchoring scheme).
        var body = SampleBody() with
        {
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections("{ch1}Thunderstorms are now expected."),
                ["es"] = SampleSections("{ch1}Ahora se esperan tormentas."),
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("ch1", ex.Message);
        Assert.Contains("changeSummary", ex.Message);
    }

    [Fact]
    public void AnchorInClosing_IsRejected()
    {
        // WX-189: anchors are forbidden in the closing too (they were always
        // forbidden there; now they are forbidden in the changeSummary as well).
        var body = SampleBody() with
        {
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections("Storms expected.") with { Closing = "{ch1}Anchors do not belong here." },
                ["es"] = SampleSections("Se esperan tormentas."),
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("closing", ex.Message);
    }

    [Fact]
    public void DuplicateSummaryToken_IsRejected()
    {
        var body = SampleBody() with
        {
            Changes = [SampleChange("ch1"), SampleChange("ch1")],
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("unique", ex.Message);
    }

    [Theory]
    [InlineData("ch0")]
    [InlineData("change1")]
    [InlineData("{ch1}")]
    public void MalformedSummaryToken_IsRejected(string token)
    {
        var body = SampleBody() with { Changes = [SampleChange(token)] };

        Assert.Throws<JsonException>(() => body.Serialize());
    }

    // ── WX-128 review hardening ───────────────────────────────────────────────

    [Fact]
    public void StaleSchemaVersion_IsRejected()
    {
        // Pins exactly to the current version — no legacy to tolerate (unlike
        // ForecastSnapshotBody, whose old persisted rows must keep loading).
        var json = SampleBody().Serialize().Replace("\"schemaVersion\":5", "\"schemaVersion\":2");

        var ex = Assert.Throws<JsonException>(() => StructuredReportBody.Deserialize(json));
        Assert.Contains("schemaVersion", ex.Message);
    }

    [Fact]
    public void IntegerEnumValue_IsRejected()
    {
        // allowIntegerValues:false — a numeric tier from Claude must fail, not
        // cast blindly to an undefined enum member the renderer can't style.
        var json = SampleBody().Serialize().Replace("\"tier\":\"plans\"", "\"tier\":7");

        Assert.Throws<JsonException>(() => StructuredReportBody.Deserialize(json));
    }

    [Fact]
    public void InvertedChangeWindow_IsRejected()
    {
        var body = SampleBody() with
        {
            Changes = [SampleChange() with { Window = new ChangeWindow(AnchorUtc.AddHours(6), AnchorUtc) }],
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("inverted window", ex.Message);
    }

    [Fact]
    public void UnbalancedBrace_IsRejected()
    {
        // A token missing its closing brace is invisible to the balanced-span
        // scan; the stray-brace check keeps it from reaching prose as literal text.
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = $"{LongProse} Highs near {{q:temp:33.5 this afternoon." },
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("unbalanced", ex.Message);
    }

    [Theory]
    [InlineData("{q:time:21:00}")]
    [InlineData("{q:time:06/08/2026}")]
    [InlineData("{q:time:2026-06-08T21:00:00}")]
    public void LooseTimeToken_IsRejected(string token)
    {
        // Ambiguous timestamps resolve to different instants depending on the
        // day they are re-parsed — the renderer runs later than the reconciler.
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = $"{LongProse} Storms arriving after {token}." },
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("ISO-8601", ex.Message);
    }

    [Theory]
    [InlineData("visibility is down to 1.5 miles")]
    [InlineData("winds near 47 mph this evening")]
    [InlineData("una visibilidad de 2 kilómetros")]
    public void BareNumberWithUnit_IsRejected(string prose)
    {
        // The renderer cannot convert plain prose — Claude's second live
        // recording wrote "1.5 miles" into a supposedly unit-neutral narrative.
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = $"{LongProse} Note that {prose}." },
            },
        };

        var ex = Assert.Throws<JsonException>(() => body.Serialize());
        Assert.Contains("plain prose", ex.Message);
    }

    [Fact]
    public void HumidityPercentage_IsAccepted()
    {
        // "%" is universal — no conversion needed, so bare percentages stay legal.
        var body = SampleBody() with
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = SampleSections(null) with { Closing = $"{LongProse} The air is humid at 88%." },
            },
        };

        var roundtripped = StructuredReportBody.Deserialize(body.Serialize());

        Assert.Contains("88%", roundtripped.Narrative["en"].Closing);
    }

    // ── VisibleLength (the WX-120-style degeneracy measure) ──────────────────

    [Fact]
    public void VisibleLength_AnchorsVanish_QuantitiesCountNominally()
    {
        // Anchor contributes nothing; each quantity/time token counts as the
        // two-char nominal stand-in; prose counts as itself.
        Assert.Equal(0, ReportTokens.VisibleLength("{ch1}"));
        Assert.Equal(2, ReportTokens.VisibleLength("{q:temp:33.5}"));
        Assert.Equal("Near 00 today.".Length, ReportTokens.VisibleLength("{ch1}Near {q:temp:33.5} today."));
        Assert.Equal(0, ReportTokens.VisibleLength("   "));
    }

    // ── Substitute: WX-203 doubled-period collapse ───────────────────────────

    [Fact]
    public void Substitute_EsTimeAtSentenceEnd_CollapsesDoubledPeriod()
    {
        // The es time designator ends in a period ("p. m."); a sentence ending right
        // on the token would double it ("…p. m..") — collapse to a single period.
        var result = ReportTokens.Substitute(
            "Despejando tras {q:time:2026-06-20T02:00:00Z}.",
            (_, _) => "X",
            _ => "9:00 p. m.");

        Assert.Equal("Despejando tras 9:00 p. m.", result);
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void Substitute_Ellipsis_IsPreserved()
    {
        // An intentional ellipsis (a run of three) is not the token/sentence collision
        // and must survive the collapse untouched.
        var result = ReportTokens.Substitute(
            "Storms possible... then clearing.",
            (_, _) => "X",
            _ => "noon");

        Assert.Contains("...", result);
    }

    [Fact]
    public void Substitute_EnTimeAtSentenceEnd_Unaffected()
    {
        // en's "9:00 PM" has no trailing period, so the sentence period stands alone —
        // nothing to collapse, and the rule never fires for English.
        var result = ReportTokens.Substitute(
            "Clearing after {q:time:2026-06-20T02:00:00Z}.",
            (_, _) => "X",
            _ => "9:00 PM");

        Assert.Equal("Clearing after 9:00 PM.", result);
    }

    [Fact]
    public void Substitute_PeriodTokenMidSentence_NotCollapsed()
    {
        // A period-ending token NOT at sentence end (its period is followed by a
        // space, then more prose) does not double — left exactly as rendered.
        var result = ReportTokens.Substitute(
            "By {q:time:2026-06-20T02:00:00Z} skies clear.",
            (_, _) => "X",
            _ => "9:00 p. m.");

        Assert.Equal("By 9:00 p. m. skies clear.", result);
    }

    [Fact]
    public void Substitute_MultipleCollisions_AllCollapse()
    {
        // The collapse is global, so two period-ending tokens each ending a sentence
        // both get fixed in one pass.
        var result = ReportTokens.Substitute(
            "Despejando tras {q:time:2026-06-20T02:00:00Z}. Lluvia hasta {q:time:2026-06-20T14:00:00Z}.",
            (_, _) => "X",
            _ => "9:00 p. m.");

        Assert.DoesNotContain("..", result);
        Assert.Equal("Despejando tras 9:00 p. m. Lluvia hasta 9:00 p. m.", result);
    }

    [Fact]
    public void Substitute_RunOfThreeOrMore_LeftIntact()
    {
        // Only an isolated pair collapses. A run of four (a period-ending token then an
        // ellipsis) is genuinely ambiguous — abbreviation+ellipsis (→ three) vs a
        // deliberate ellipsis+period (keep four) — so we do NOT guess; the run is left
        // exactly as authored, just as a bare ellipsis is.
        var result = ReportTokens.Substitute(
            "Despejando tras {q:time:2026-06-20T02:00:00Z}...",
            (_, _) => "X",
            _ => "9:00 p. m.");

        Assert.Equal("Despejando tras 9:00 p. m....", result);
    }

    // ── WX-228: the temp_range token (a two-endpoint °C span) ────────────────

    [Fact]
    public void Validate_TempRange_WellFormed_Accepted() =>
        // A 'lo:hi' pair of finite numbers validates like any other token (no throw).
        ReportTokens.ValidateAndCollectAnchors("Highs hold {q:temp_range:24:26} all week.", "closing");

    [Theory]
    [InlineData("{q:temp_range:24}")]      // only one endpoint
    [InlineData("{q:temp_range:24:}")]     // missing high
    [InlineData("{q:temp_range::26}")]     // missing low
    [InlineData("{q:temp_range:a:b}")]     // non-numeric
    [InlineData("{q:temp_range:24:26:28}")] // three values
    [InlineData("{q:temp_range:Infinity:30}")] // non-finite low (parses, but IsFinite rejects)
    [InlineData("{q:temp_range:1:NaN}")]   // non-finite high
    public void Validate_TempRange_Malformed_Throws(string token) =>
        Assert.Throws<JsonException>(() => ReportTokens.ValidateAndCollectAnchors(token, "closing"));

    [Fact]
    public void Substitute_TempRange_RendersViaRangeCallback()
    {
        var result = ReportTokens.Substitute(
            "Highs reach {q:temp_range:36:37} this week.",
            (_, _) => "X",
            _ => "noon",
            (lo, hi) => $"[{lo}-{hi}]");

        Assert.Equal("Highs reach [36-37] this week.", result);
    }

    [Fact]
    public void Substitute_TempRange_NoRangeCallback_LeftVerbatim()
    {
        // The three-argument overload (callers with no range renderer) leaves a temp_range
        // token untouched rather than crashing — substitution must never throw on a send.
        var result = ReportTokens.Substitute(
            "Highs reach {q:temp_range:36:37}.",
            (_, _) => "X",
            _ => "noon");

        Assert.Equal("Highs reach {q:temp_range:36:37}.", result);
    }
}