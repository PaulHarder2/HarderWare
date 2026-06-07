using System.Text.Json;
using System.Text.Json.Serialization;

using WxServices.Common;

namespace MetarParser.Data.Entities;

/// <summary>
/// Single serialization policy for the canonical body JSON persisted by both
/// <see cref="ForecastSnapshotBody"/> and <see cref="StructuredReportBody"/>.
/// The two bodies travel in the same tool_use envelope and version in lockstep
/// (WX-128), so their wire conventions are owned in one place: camelCase
/// properties, snake_case enum strings (integer enum values rejected — a
/// numeric tier/phenomenon from Claude must fail, not cast blindly), nulls
/// omitted.
/// </summary>
internal static class CanonicalBodyJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>
/// Strongly-typed in-memory representation of the JSON persisted in the
/// <see cref="ForecastSnapshot.Body"/> column.  A body is a complete forecast
/// snapshot for one station at one moment: a schema-versioned envelope around
/// an ordered list of uniform 6-hour <see cref="ForecastSnapshotBlock"/>s
/// covering up to a six-day horizon.
///
/// Defined under WX-76 as the foundation for the WX-47 rearchitecture.  The
/// human-readable specification lives at
/// <c>WxServices/docs/forecast-snapshot-schema.md</c>.
/// </summary>
public sealed record ForecastSnapshotBody
{
    /// <summary>
    /// Current schema version produced by this build.  Bumped only when the
    /// body shape changes incompatibly — and kept in lockstep with
    /// <see cref="StructuredReportBody.SchemaVersionCurrent"/> since WX-128:
    /// the two bodies travel in the same tool_use envelope, so a change to
    /// either bumps both ("suspenders and belt").  v3 left this body's shape
    /// unchanged; the bump marks the envelope gaining structured_report.
    /// </summary>
    public const int SchemaVersionCurrent = 3;

    /// <summary>
    /// Schema version this body conforms to.  On write, defaults to
    /// <see cref="SchemaVersionCurrent"/>.  On read, callers may inspect it and
    /// dispatch version-aware logic.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SchemaVersionCurrent;

    /// <summary>
    /// Ordered list of 6-hour blocks, earliest first.  Empty for a freshly
    /// constructed body; populated by the GFS-to-provisional builder (WX-77)
    /// and refined by the Claude two-pass integration (WX-79).
    /// </summary>
    [JsonPropertyName("blocks")]
    public IReadOnlyList<ForecastSnapshotBlock> Blocks { get; init; } = [];

    /// <summary>Serialize to the canonical JSON form persisted in the DB column.  Calls <see cref="Validate"/> first so malformed bodies cannot be persisted.</summary>
    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, CanonicalBodyJson.Options);
    }

    /// <summary>
    /// Deserialize from JSON.  Throws <see cref="JsonException"/> on invalid
    /// input (unknown enum values, malformed JSON, missing required fields,
    /// type mismatches, or violation of the precipPhenomenon-iff-non-none
    /// invariant — see <see cref="Validate"/>).
    /// </summary>
    public static ForecastSnapshotBody Deserialize(string json)
    {
        var body = JsonSerializer.Deserialize<ForecastSnapshotBody>(json, CanonicalBodyJson.Options)
            ?? throw new JsonException("Deserialized snapshot body was null.");
        body.Validate();
        return body;
    }

    /// <summary>
    /// Enforce the precipPhenomenon-iff-non-none invariant: every block must
    /// have a non-null <see cref="ForecastSnapshotBlock.PrecipPhenomenon"/>
    /// exactly when its <see cref="ForecastSnapshotBlock.PrecipExpectation"/>
    /// is not <see cref="PrecipExpectation.None"/>.  Called from both
    /// <see cref="Serialize"/> (so malformed bodies never reach the DB) and
    /// <see cref="Deserialize"/> (so malformed JSON never returns as a valid
    /// object), so neither persistence nor reads can produce semantically
    /// invalid bodies.  Throws <see cref="JsonException"/> on violation.
    /// </summary>
    private void Validate()
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            var block = Blocks[i];
            bool isNone = block.PrecipExpectation == PrecipExpectation.None;
            bool hasPhenomenon = block.PrecipPhenomenon is not null;
            if (isNone == hasPhenomenon)
            {
                throw new JsonException(
                    $"Block {i} (startUtc={block.StartUtc:O}) violates the precipPhenomenon invariant: " +
                    $"precipExpectation={block.PrecipExpectation}, precipPhenomenon={block.PrecipPhenomenon?.ToString() ?? "null"}. " +
                    "precipPhenomenon must be null exactly when precipExpectation is None.");
            }
        }
    }

    /// <summary>
    /// Temperature drift (°C) below which two blocks are treated as materially the
    /// same. 2 °C absorbs ordinary model/observation wobble between hourly cycles —
    /// below the threshold where a recipient would perceive the forecast band as
    /// having changed — while still surfacing a real warm-up or cool-down.
    /// </summary>
    private const double TempToleranceC = 2.0;

    /// <summary>
    /// Wind drift (kt) below which two blocks are treated as materially the same.
    /// 5 kt covers METAR wind rounding and minor model jitter (a non-actionable
    /// breeze change) while still catching a shift to a genuinely windier regime.
    /// Robust near a band boundary, where a 1–2 kt change would otherwise flip
    /// <see cref="WindBand"/>.
    /// </summary>
    private const int WindToleranceKt = 5;

    /// <summary>
    /// Reader-facing material equality used by the WX-108 redundancy backstop:
    /// true when this body says the same thing a reader would act on as
    /// <paramref name="other"/>.  Categorical fields (sky, obscuration, precip
    /// expectation/phenomenon, severeFlag) must match exactly;
    /// temperature may drift within <see cref="TempToleranceC"/>, and wind within
    /// <see cref="WindToleranceKt"/> or while staying in the same
    /// <see cref="WxServices.Common.WindScale"/> impact band.  Blocks are
    /// matched by <see cref="ForecastSnapshotBlock.StartUtc"/>; horizon-edge
    /// blocks that rolled on or off with the passage of time are not, by
    /// themselves, treated as news, so only the overlapping blocks are compared.
    /// </summary>
    public bool MateriallyEquals(ForecastSnapshotBody other) => MaterialMatch(other, ignoreSevere: false);

    /// <summary>
    /// As <see cref="MateriallyEquals"/>, but ignores <see cref="ForecastSnapshotBlock.SevereFlag"/>.
    /// Used by the WX-108 severe-flag hysteresis: when the two bodies differ
    /// <em>only</em> by severe flags and no newer GFS run or TAF has arrived since
    /// the last sent report, the flip is observation-driven and not trusted as
    /// send-worthy news (severe potential comes from model/TAF guidance, which an
    /// hourly METAR cannot change).
    /// </summary>
    public bool MateriallyEqualsIgnoringSevere(ForecastSnapshotBody other) => MaterialMatch(other, ignoreSevere: true);

    private bool MaterialMatch(ForecastSnapshotBody other, bool ignoreSevere)
    {
        ArgumentNullException.ThrowIfNull(other);

        var theirs = new Dictionary<DateTime, ForecastSnapshotBlock>(other.Blocks.Count);
        foreach (var b in other.Blocks)
            theirs[b.StartUtc] = b;

        bool anyShared = false;
        foreach (var a in Blocks)
        {
            if (!theirs.TryGetValue(a.StartUtc, out var b))
                continue;
            anyShared = true;
            if (!BlocksMateriallyEqual(a, b, ignoreSevere))
                return false;
        }

        // No overlapping blocks: equal only when both bodies are empty. An
        // empty-vs-populated pair (or two fully disjoint horizons) is a real
        // change, not a redundant re-send.
        if (!anyShared)
            return Blocks.Count == 0 && other.Blocks.Count == 0;

        return true;
    }

    /// <summary>
    /// True when this body raises <see cref="ForecastSnapshotBlock.SevereFlag"/> on
    /// any block that <paramref name="prior"/> did not flag (a matching block prior
    /// left unflagged, or a block prior did not cover) — i.e. a severe *escalation*.
    /// The WX-108 hysteresis uses this so it suppresses only severe *de-escalations*
    /// on an observation-only advance (the untrusted whipsaw, e.g. the 06-02
    /// 348→363 drop); the *arrival* of a new severe hazard is always news worth
    /// sending, even on a bare observation (directional asymmetry).
    /// </summary>
    public bool HasSevereEscalationOver(ForecastSnapshotBody prior)
    {
        ArgumentNullException.ThrowIfNull(prior);
        var priorBlocks = new Dictionary<DateTime, ForecastSnapshotBlock>(prior.Blocks.Count);
        foreach (var b in prior.Blocks)
            priorBlocks[b.StartUtc] = b;

        foreach (var b in Blocks)
            if (b.SevereFlag && (!priorBlocks.TryGetValue(b.StartUtc, out var p) || !p.SevereFlag))
                return true;
        return false;
    }

    private static bool BlocksMateriallyEqual(ForecastSnapshotBlock a, ForecastSnapshotBlock b, bool ignoreSevere) =>
        a.SkyState == b.SkyState
        && a.Obscuration == b.Obscuration
        && a.PrecipExpectation == b.PrecipExpectation
        && a.PrecipPhenomenon == b.PrecipPhenomenon
        && (ignoreSevere || a.SevereFlag == b.SevereFlag)
        && Math.Abs(a.TemperatureCelsius.Min - b.TemperatureCelsius.Min) <= TempToleranceC
        && Math.Abs(a.TemperatureCelsius.Max - b.TemperatureCelsius.Max) <= TempToleranceC
        && WindEndpointEqual(a.WindKt.Min, b.WindKt.Min)
        && WindEndpointEqual(a.WindKt.Max, b.WindKt.Max);

    // Two wind endpoints are materially equal when within the absolute tolerance
    // (small drift, robust across a band boundary) OR in the same shared impact band
    // (WxServices.Common.WindScale — larger drift the public would still shrug at,
    // e.g. 6 kt vs 15 kt). WX-110: the band test is what suppresses the
    // trivial-wobble redundant re-sends the 5 kt tolerance alone missed. The same-
    // band test is capped at MaxInBandDriftKt so the open-ended top band (64+ kt)
    // can't equate, say, 65 kt and 150 kt and suppress a genuine hurricane-force
    // escalation as redundant (CodeRabbit) — every bounded band spans ≤16 kt, so the
    // cap never restricts a legitimate within-band match.
    private static bool WindEndpointEqual(int a, int b) =>
        Math.Abs(a - b) <= WindToleranceKt
        || (WindScale.Band(a) == WindScale.Band(b) && Math.Abs(a - b) <= MaxInBandDriftKt);

    // Widest bounded WindScale band spans 16 kt (bands 0 = 0–16 and 1 = 17–33).
    // Caps how much the same-band test absorbs, bounding the open-ended 64+ band
    // without restricting any bounded band.
    private const int MaxInBandDriftKt = 16;
}

/// <summary>One uniform 6-hour block within a <see cref="ForecastSnapshotBody"/>.</summary>
public sealed record ForecastSnapshotBlock
{
    /// <summary>UTC start of the block, aligned to 00/06/12/18Z.  Block end is implicitly <c>StartUtc + 6h</c>.</summary>
    [JsonPropertyName("startUtc")]
    public required DateTime StartUtc { get; init; }

    /// <summary>Dominant cloud-cover state.  Fog and other obscurations are tracked separately in <see cref="Obscuration"/>.</summary>
    [JsonPropertyName("skyState")]
    public required SkyState SkyState { get; init; }

    /// <summary>Any obscuration to visibility independent of cloud cover (fog, haze, smoke, dust).  Defaults to <see cref="Obscuration.None"/>.</summary>
    [JsonPropertyName("obscuration")]
    public required Obscuration Obscuration { get; init; }

    /// <summary>Min/max temperature across the block, in degrees Celsius.  Recipient unit preferences are applied at email-render time.</summary>
    [JsonPropertyName("temperatureCelsius")]
    public required MinMax<double> TemperatureCelsius { get; init; }

    /// <summary>Min/max sustained wind across the block, in knots (matching the METAR/TAF native unit).</summary>
    [JsonPropertyName("windKt")]
    public required MinMax<int> WindKt { get; init; }

    /// <summary>How likely precipitation is during the block.</summary>
    [JsonPropertyName("precipExpectation")]
    public required PrecipExpectation PrecipExpectation { get; init; }

    /// <summary>What kind of precipitation is expected, when any is expected.  Must be <see langword="null"/> exactly when <see cref="PrecipExpectation"/> is <see cref="PrecipExpectation.None"/>.</summary>
    [JsonPropertyName("precipPhenomenon")]
    public PrecipPhenomenon? PrecipPhenomenon { get; init; }

    /// <summary>Safety-critical flag.  The population rules live in WX-81 (significance-tier prompting); WX-76 reserves the column.</summary>
    [JsonPropertyName("severeFlag")]
    public required bool SevereFlag { get; init; }
}

/// <summary>Inclusive min/max pair of a numeric block-level quantity.  Value-typed for low allocation overhead in 24-block snapshots.</summary>
public readonly record struct MinMax<T>(
    [property: JsonPropertyName("min")] T Min,
    [property: JsonPropertyName("max")] T Max
) where T : struct;

/// <summary>Dominant cloud-cover state for a block.  Fog and other obscurations live in <see cref="Obscuration"/>.</summary>
public enum SkyState
{
    Clear,
    PartlyCloudy,
    MostlyCloudy,
    Overcast,
}

/// <summary>Visibility-reducing phenomena that aren't precipitation.</summary>
public enum Obscuration
{
    None,
    Fog,
    Haze,
    Smoke,
    Dust,
}

/// <summary>How likely precipitation is in a block.</summary>
public enum PrecipExpectation
{
    None,
    Possible,
    Likely,
    Certain,
}

/// <summary>
/// What kind of precipitation is expected.  Each value corresponds to a
/// distinct reasoning bucket for downstream tier routing (WX-81):
/// <list type="bullet">
/// <item><see cref="Rain"/> — liquid precipitation.</item>
/// <item><see cref="Thunderstorm"/> — convective; safety-critical via lightning, hail, downbursts.</item>
/// <item><see cref="Mixed"/> — rain/snow mix that isn't ice-forming on contact.</item>
/// <item><see cref="Snow"/> — snow as such.</item>
/// <item><see cref="FreezingPrecip"/> — freezing rain, freezing drizzle, and sleet.  Bucketed together because they are equally road-slickening and equally safety-critical; the distinction between them rarely affects the reader's decision.</item>
/// </list>
/// </summary>
public enum PrecipPhenomenon
{
    Rain,
    Thunderstorm,
    Mixed,
    Snow,
    FreezingPrecip,
}