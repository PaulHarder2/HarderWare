using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetarParser.Data.Entities;

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
    /// <summary>Current schema version produced by this build.  Bumped only when the body shape changes incompatibly.</summary>
    public const int SchemaVersionCurrent = 1;

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

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Serialize to the canonical JSON form persisted in the DB column.  Calls <see cref="Validate"/> first so malformed bodies cannot be persisted.</summary>
    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, SerializerOptions);
    }

    /// <summary>
    /// Deserialize from JSON.  Throws <see cref="JsonException"/> on invalid
    /// input (unknown enum values, malformed JSON, missing required fields,
    /// type mismatches, or violation of the precipPhenomenon-iff-non-none
    /// invariant — see <see cref="Validate"/>).
    /// </summary>
    public static ForecastSnapshotBody Deserialize(string json)
    {
        var body = JsonSerializer.Deserialize<ForecastSnapshotBody>(json, SerializerOptions)
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

    /// <summary>Qualitative gust outlook for the block.  Categorical to fit the "Claude's working memory" framing of WX-47; specific gust numbers may surface in the email narrative when warranted.</summary>
    [JsonPropertyName("gustOutlook")]
    public required GustOutlook GustOutlook { get; init; }

    /// <summary>How likely precipitation is during the block.</summary>
    [JsonPropertyName("precipExpectation")]
    public required PrecipExpectation PrecipExpectation { get; init; }

    /// <summary>What kind of precipitation is expected, when any is expected.  Must be <see langword="null"/> exactly when <see cref="PrecipExpectation"/> is <see cref="PrecipExpectation.None"/>.</summary>
    [JsonPropertyName("precipPhenomenon")]
    public PrecipPhenomenon? PrecipPhenomenon { get; init; }

    /// <summary>Safety-critical flag.  The population rules live in WX-81 (significance-tier prompting); WX-76 reserves the column.</summary>
    [JsonPropertyName("severeFlag")]
    public required bool SevereFlag { get; init; }

    /// <summary>Qualitative visibility outlook.  Three tiers chosen for plans-affecting granularity at 6-hour resolution.</summary>
    [JsonPropertyName("visibilityExpectation")]
    public required VisibilityExpectation VisibilityExpectation { get; init; }
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

/// <summary>Qualitative gust outlook for a block.</summary>
public enum GustOutlook
{
    None,
    Occasional,
    Frequent,
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

/// <summary>Qualitative visibility outlook for a block.  Three tiers chosen for plans-affecting granularity at 6-hour resolution.</summary>
public enum VisibilityExpectation
{
    Poor,
    Reduced,
    Good,
}