using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MetarParser.Data.Entities;

/// <summary>
/// Strongly-typed in-memory representation of the unit-neutral structured
/// report emitted by the per-locality Claude reconciliation (WX-128).  Where
/// <see cref="ForecastSnapshotBody"/> captures the forecast <em>state</em>
/// (6-hour blocks), this captures the report <em>content</em>: a
/// salience-ranked list of changes (language-free enums and typed quantities)
/// plus a language-keyed narrative whose prose carries quantities as
/// substitution tokens (see <see cref="ReportTokens"/>) so a deterministic
/// renderer (WX-129) can produce each recipient's report — units, locale,
/// and language — without re-invoking the LLM.
///
/// <para>
/// SchemaVersion moves in lockstep with
/// <see cref="ForecastSnapshotBody.SchemaVersionCurrent"/>: both were set to 3
/// when this type was introduced, and any future shape change to either bumps
/// both ("suspenders and belt", WX-128).
/// </para>
/// </summary>
public sealed record StructuredReportBody
{
    /// <summary>Current schema version produced by this build.  Lockstep with <see cref="ForecastSnapshotBody.SchemaVersionCurrent"/> — enforced by construction, not by convention.</summary>
    public const int SchemaVersionCurrent = ForecastSnapshotBody.SchemaVersionCurrent;

    /// <summary>Schema version this body conforms to.  On write, defaults to <see cref="SchemaVersionCurrent"/>.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = SchemaVersionCurrent;

    /// <summary>
    /// Salience-ranked changes versus the prior committed forecast, most
    /// important first.  Empty on cycles where nothing changed (e.g. a routine
    /// scheduled send with a steady forecast).  Each entry is language-free:
    /// enums plus canonical-unit quantities.
    /// </summary>
    [JsonPropertyName("changes")]
    public IReadOnlyList<ReportChange> Changes { get; init; } = [];

    /// <summary>
    /// Narrative prose keyed by ISO 639-1 language code (e.g. <c>"en"</c>,
    /// <c>"es"</c>).  One entry per language requested for the locality's
    /// recipients; quantities appear as <see cref="ReportTokens"/> tokens, never
    /// as literal unit-bound text.  Which languages must be present is a
    /// per-call contract enforced by the reconciler; this type validates only
    /// the intrinsic shape (keys well-formed, sections present, tokens parse).
    /// </summary>
    [JsonPropertyName("narrative")]
    public IReadOnlyDictionary<string, NarrativeSections> Narrative { get; init; } =
        new Dictionary<string, NarrativeSections>();

    /// <summary>Serialize to the canonical JSON form persisted in the DB column.  Calls <see cref="Validate"/> first so malformed bodies cannot be persisted.</summary>
    public string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, CanonicalBodyJson.Options);
    }

    /// <summary>
    /// Deserialize from JSON.  Throws <see cref="JsonException"/> on invalid
    /// input — malformed JSON, unknown enum values, missing sections, malformed
    /// or dangling substitution tokens (see <see cref="Validate"/>).
    /// </summary>
    public static StructuredReportBody Deserialize(string json)
    {
        var body = JsonSerializer.Deserialize<StructuredReportBody>(json, CanonicalBodyJson.Options)
            ?? throw new JsonException("Deserialized structured report body was null.");
        body.Validate();
        return body;
    }

    private static readonly Regex LanguageKeyPattern = new("^[a-z]{2}$", RegexOptions.Compiled);

    /// <summary>
    /// Enforce the intrinsic invariants, throwing <see cref="JsonException"/>
    /// on violation.  Called from both <see cref="Serialize"/> and
    /// <see cref="Deserialize"/> so neither persistence nor reads can produce a
    /// semantically invalid body:
    /// <list type="bullet">
    /// <item>at least one narrative language; keys are ISO 639-1 codes;</item>
    /// <item>each language has a non-blank closing section (the only required section since v4);</item>
    /// <item>change summaryTokens are well-formed (<c>ch1</c>, <c>ch2</c>, …) and unique;</item>
    /// <item>since WX-189 the change set is computed deterministically rather than narrated
    /// by anchors, so <c>{chN}</c> anchors are forbidden in BOTH prose sections (the earlier
    /// "every change narrated / no dangling anchor" correspondence is retired); every brace
    /// token in every section still parses per the <see cref="ReportTokens"/> grammar.</item>
    /// </list>
    /// </summary>
    private void Validate()
    {
        // The version pins exactly: a freshly-produced report must carry the current
        // version (Claude copying the prior snapshot's older version digit fails closed
        // here).  Persisted reports ARE re-deserialized in one place — the WX-182 cached
        // re-send — so a release that tightens these rules (e.g. WX-189 forbidding {chN}
        // anchors older bodies carry) can reject a legacy persisted body; that path treats
        // a deserialize failure as "reconcile instead of re-send", so it self-heals.
        // (Born at v3 in WX-128; bumped to v4 in WX-130; v5 in WX-155.)
        if (SchemaVersion != SchemaVersionCurrent)
            throw new JsonException(
                $"Structured report schemaVersion {SchemaVersion} is not the supported version {SchemaVersionCurrent}.");

        if (Narrative.Count == 0)
            throw new JsonException("Structured report has no narrative languages.");

        var changeTokens = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Changes.Count; i++)
        {
            var change = Changes[i];
            var token = change.SummaryToken;
            if (!ReportTokens.IsWellFormedAnchorName(token))
                throw new JsonException($"Change {i} has a malformed summaryToken '{token}' (expected 'ch<n>').");
            if (!changeTokens.Add(token))
                throw new JsonException($"Change {i} reuses summaryToken '{token}'; tokens must be unique.");
            if (change.Window.EndUtc < change.Window.StartUtc)
                throw new JsonException(
                    $"Change {i} ('{token}') has an inverted window: endUtc {change.Window.EndUtc:O} precedes startUtc {change.Window.StartUtc:O}.");
        }

        foreach (var (lang, sections) in Narrative)
        {
            if (!LanguageKeyPattern.IsMatch(lang))
                throw new JsonException($"Narrative key '{lang}' is not an ISO 639-1 language code.");

            RequireSection(lang, "closing", sections.Closing);

            // WX-189: the change set is computed deterministically from
            // (prior, final_snapshot) after the reconciliation call, not narrated
            // by {chN} anchors, so neither prose section carries anchors. Both are
            // still validated for token grammar ({q:...}); a stray anchor in either
            // is now an error (it would render to nothing and tie to nothing). The
            // earlier "every change narrated / no dangling anchor" correspondence is
            // retired with the anchoring scheme.
            ForbidAnchors(lang, "changeSummary", sections.ChangeSummary ?? "");
            ForbidAnchors(lang, "closing", sections.Closing);
        }
    }

    private static void RequireSection(string lang, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException($"narrative.{lang}.{name} is missing or blank.");
    }

    private static void ForbidAnchors(string lang, string name, string value)
    {
        var anchors = ReportTokens.ValidateAndCollectAnchors(value, $"narrative.{lang}.{name}");
        if (anchors.Count > 0)
            throw new JsonException(
                $"narrative.{lang}.{name} contains change anchor '{anchors.First()}'; change anchors are no longer used (WX-189 — the change set is computed deterministically) — write plain prose.");
    }
}

/// <summary>
/// The narrative prose for one language (WX-130): the two <em>judgment</em>
/// sections the WX-129 renderer drops into the report scaffold (substituting
/// <c>{q:...}</c> tokens) — the change-summary band and the closing summary.
/// The Current Conditions table and the per-day Extended Forecast grid are
/// rebuilt <em>deterministically</em> from the observation and the reconciled
/// snapshot, so they carry no narrative prose (the WX-128 currentConditions /
/// extendedForecast sections were dropped at v4).
/// </summary>
public sealed record NarrativeSections
{
    /// <summary>
    /// Prose for the "What's changed:" band.  <see langword="null"/> when the
    /// cycle carries no change worth a band (typical scheduled send).  This is
    /// the only section in which <c>{chN}</c> anchors may appear; when the
    /// parent body's changes list is non-empty, every change's anchor must
    /// appear here in every language.
    /// </summary>
    [JsonPropertyName("changeSummary")]
    public string? ChangeSummary { get; init; }

    /// <summary>Prose for the "In summary:" closing.</summary>
    [JsonPropertyName("closing")]
    public required string Closing { get; init; }
}

/// <summary>
/// One salience-ranked change versus the prior committed forecast —
/// language-free facts the renderer can style (band ordering, tier emphasis)
/// without parsing prose.  The vocabulary follows the WX-81 significance
/// hierarchy (<see cref="ChangeTier"/>) and adds the direction of travel,
/// including the WX-111 vector case (<see cref="ChangeDirection.Shifting"/> /
/// <see cref="ChangePhenomenon.WindShift"/>).
/// </summary>
public sealed record ReportChange
{
    /// <summary>Significance tier per the WX-81 hierarchy.</summary>
    [JsonPropertyName("tier")]
    public required ChangeTier Tier { get; init; }

    /// <summary>What kind of weather is changing.</summary>
    [JsonPropertyName("phenomenon")]
    public required ChangePhenomenon Phenomenon { get; init; }

    /// <summary>Which way the change runs (the directional-asymmetry input: appearing weather outranks clearing weather).</summary>
    [JsonPropertyName("direction")]
    public required ChangeDirection Direction { get; init; }

    /// <summary>UTC window the change affects.</summary>
    [JsonPropertyName("window")]
    public required ChangeWindow Window { get; init; }

    /// <summary>
    /// Typed quantities characterising the change, in canonical units per
    /// <see cref="QuantityKind"/>.  May be empty for purely categorical changes
    /// (e.g. fog clearing).
    /// </summary>
    [JsonPropertyName("quantities")]
    public IReadOnlyList<ReportQuantity> Quantities { get; init; } = [];

    /// <summary>
    /// Anchor name (<c>ch1</c>, <c>ch2</c>, …) tying this change to its
    /// sentence in every language's changeSummary, where it appears wrapped as
    /// <c>{ch1}</c>.  Unique within the body.
    /// </summary>
    [JsonPropertyName("summaryToken")]
    public required string SummaryToken { get; init; }
}

/// <summary>Inclusive UTC window a <see cref="ReportChange"/> affects.</summary>
public readonly record struct ChangeWindow(
    [property: JsonPropertyName("startUtc")] DateTime StartUtc,
    [property: JsonPropertyName("endUtc")] DateTime EndUtc);

/// <summary>One typed quantity on a change, in the canonical unit its <see cref="QuantityKind"/> defines.</summary>
public sealed record ReportQuantity
{
    /// <summary>Which quantity this is; fixes the canonical unit.</summary>
    [JsonPropertyName("kind")]
    public required QuantityKind Kind { get; init; }

    /// <summary>Value in the kind's canonical unit.</summary>
    [JsonPropertyName("value")]
    public required double Value { get; init; }
}

/// <summary>Significance tier of a change, mirroring the WX-81 hierarchy used in the reconciliation guidance.</summary>
public enum ChangeTier
{
    /// <summary>Safety-critical — hazards worth a prompt send at any horizon.</summary>
    Safety,

    /// <summary>Plans-affecting — worth an unscheduled send when near-horizon.</summary>
    Plans,

    /// <summary>Ambient-interest — scheduled sends only.</summary>
    Ambient,
}

/// <summary>
/// What kind of weather a <see cref="ReportChange"/> concerns.  Precipitation
/// values mirror <see cref="PrecipPhenomenon"/>; obscurations mirror
/// <see cref="Obscuration"/>; the rest cover wind (speed and the WX-111
/// vector-shift case), temperature swings, and severe-potential changes that
/// aren't tied to a single precipitation type.
/// </summary>
public enum ChangePhenomenon
{
    Rain,
    Thunderstorm,
    Mixed,
    Snow,
    FreezingPrecip,
    Wind,
    WindShift,
    Fog,
    Haze,
    Smoke,
    Dust,
    Temperature,
    Severe,
}

/// <summary>Which way a change runs.  Appearing/clearing carry the directional asymmetry; shifting is the WX-111 vector case.</summary>
public enum ChangeDirection
{
    Appearing,
    Strengthening,
    Weakening,
    Clearing,
    Shifting,
}

/// <summary>
/// Which quantity a <see cref="ReportQuantity"/> or <c>{q:...}</c> token
/// carries.  Each kind fixes its canonical unit — the unit is implied, never
/// written — so the token grammar stays small and the renderer's conversion
/// table is keyed by kind alone:
/// <list type="bullet">
/// <item><see cref="Temp"/> — degrees Celsius.</item>
/// <item><see cref="Wind"/> — sustained wind, knots.</item>
/// <item><see cref="Gust"/> — gust, knots.</item>
/// <item><see cref="Pressure"/> — hectopascals (hPa).</item>
/// <item><see cref="PrecipMm"/> — liquid-equivalent accumulation, millimetres.</item>
/// </list>
/// </summary>
public enum QuantityKind
{
    Temp,
    Wind,
    Gust,
    Pressure,
    PrecipMm,
}

/// <summary>
/// The WX-128 substitution-token grammar shared by the structured report's
/// validator (this assembly) and the deterministic renderer (WX-129).  Three
/// token forms may appear in narrative prose:
/// <list type="bullet">
/// <item><c>{q:&lt;kind&gt;:&lt;value&gt;}</c> — a quantity in its kind's canonical
/// unit (period decimal separator), e.g. <c>{q:temp:33.5}</c>, <c>{q:gust:30}</c>.
/// Rendered in the recipient's units and locale.</item>
/// <item><c>{q:time:&lt;ISO-8601 UTC&gt;}</c> — an instant, e.g.
/// <c>{q:time:2026-06-08T21:00:00Z}</c>.  Rendered in the locality timezone and
/// the recipient's locale.</item>
/// <item><c>{ch&lt;n&gt;}</c> — an invisible anchor tying a changeSummary sentence
/// to the change whose summaryToken is <c>ch&lt;n&gt;</c>.  Renders to nothing.</item>
/// </list>
/// Any other brace-delimited sequence is malformed and fails validation, so a
/// typo'd token can never silently reach a recipient as literal text.
/// </summary>
public static partial class ReportTokens
{
    /// <summary>
    /// Anchor-name grammar (<c>ch1</c>, <c>ch2</c>, ...), shared by the validator
    /// regexes below and the Claude tool schema's <c>summaryToken.pattern</c>
    /// so the grammar Claude is held to and the grammar enforced here cannot
    /// diverge.
    /// </summary>
    public const string AnchorNameRegexText = "ch[1-9][0-9]*";

    // The unit vocabulary the double-unit guards match: English and Spanish
    // spellings for the canonical quantity kinds plus the distance/visibility
    // units that have no token kind at all (those must be phrased
    // qualitatively). Deliberately excludes bare "mi" ("mi casa") and "ft"
    // (false-positive prone) and "%" (universal, no conversion needed). A
    // fuller per-language lexicon belongs to the WX-137 language registry when
    // it exists; until then this list is the supported-language (en/es)
    // vocabulary.
    private const string UnitWords =
        @"kts?|knots?|nudos?|mph|km/h|kph|m/s|hPa|mb|millibars?|milibares?|mm|millimet(er|re)s?|mil[iií]metros?"
        + @"|inHg|inch(es)?|pulgadas?|°\s?[CF]|deg(rees?)?\.?|grados?|cent[iií]grados?|celsius|fahrenheit"
        + @"|miles?|millas?|kilomet(er|re)s?|kil[ooó]metros?|met(er|re)s?|metros?|feet|pies";

    // Whitespace-or-NBSP separator between a token/number and a unit word —
    // Spanish typography conventionally puts a non-breaking space before unit
    // symbols, and \s does not match NBSP in .NET regex.
    private const string UnitSeparator = @"[\s\u00A0]*[-(]?[\s\u00A0]*";

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex BraceSpanPattern();

    [GeneratedRegex(@"^\{q:(?<kind>[a-z_]+):(?<value>[^{}]+)\}$")]
    private static partial Regex QuantityTokenPattern();

    [GeneratedRegex(@"^\{(?<anchor>" + AnchorNameRegexText + @")\}$")]
    private static partial Regex AnchorTokenPattern();

    [GeneratedRegex("^" + AnchorNameRegexText + "$")]
    private static partial Regex AnchorNamePattern();

    // Strict ISO-8601 UTC instant: full date, 'T', at least minutes, 'Z'. Bare
    // DateTime.TryParse would accept "21:00" or "06/08/2026" — forms that
    // resolve to a different instant depending on the day they are re-parsed,
    // which for a renderer that runs after the reconciliation is a wrong-time
    // report waiting to happen.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d+)?Z$")]
    private static partial Regex TimeValuePattern();

    // WX-203: an isolated pair of periods — a token's trailing period ("p. m.")
    // colliding with a sentence period — and NOT a run of three or more (an
    // ellipsis), which the lookarounds exclude. Used to collapse the pair to one.
    [GeneratedRegex(@"(?<!\.)\.\.(?!\.)")]
    private static partial Regex DoubledPeriodPattern();

    // A literal unit written adjacent to a *quantity* token ({q:time:...} is
    // exempt — a unit word after a time reference is not the double-unit bug).
    // The renderer appends the unit when it substitutes the token, so prose
    // like "{q:gust:41} kt" would render as "47 mph kt" for a US recipient.
    // Claude's first live call against the v3 schema produced exactly this
    // (WX-128 re-record finding), so the rule is enforced deterministically,
    // not just prompted.
    [GeneratedRegex(
        @"\{q:(?!time:)[^{}]*\}" + UnitSeparator + "(" + UnitWords + @")\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex UnitAfterTokenPattern();

    // A bare number with a literal unit and no token at all ("1.5 miles",
    // "47 mph") — the unit-bound prose class Claude's second live recording
    // exposed in currentConditions. The renderer cannot convert plain prose,
    // so a metric recipient would receive untranslated US units. Run against
    // token-stripped text only, so digits inside {q:...} values never match.
    [GeneratedRegex(
        @"\d[\d.,]*" + UnitSeparator + "(" + UnitWords + @")\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BareNumberUnitPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunPattern();

    // Derived from the QuantityKind enum via the same naming policy the
    // serializer applies, so adding a kind updates the validator automatically
    // ("time" is a token form, not a QuantityKind — handled separately).
    private static readonly IReadOnlySet<string> QuantityKinds = Enum.GetNames<QuantityKind>()
        .Select(JsonNamingPolicy.SnakeCaseLower.ConvertName)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>True when <paramref name="name"/> is a well-formed anchor name (<c>ch1</c>, <c>ch2</c>, ... — no braces).</summary>
    public static bool IsWellFormedAnchorName(string name) => AnchorNamePattern().IsMatch(name);

    /// <summary>
    /// Validates every token in <paramref name="text"/> against the grammar and
    /// returns the set of anchor names used.  Throws <see cref="JsonException"/>
    /// naming <paramref name="where"/> on the first violation — unknown quantity
    /// kind, unparsable value, non-ISO-8601-UTC timestamp, a brace span matching
    /// no token form, a stray unbalanced brace, a literal unit adjacent to a
    /// quantity token, or a bare number-with-unit written as plain prose.
    /// </summary>
    public static IReadOnlySet<string> ValidateAndCollectAnchors(string text, string where)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match span in BraceSpanPattern().Matches(text))
        {
            var anchor = AnchorTokenPattern().Match(span.Value);
            if (anchor.Success)
            {
                anchors.Add(anchor.Groups["anchor"].Value);
                continue;
            }

            var quantity = QuantityTokenPattern().Match(span.Value);
            if (!quantity.Success)
                throw new JsonException($"{where} contains malformed token '{span.Value}'.");

            var kind = quantity.Groups["kind"].Value;
            var value = quantity.Groups["value"].Value;
            if (kind == "time")
            {
                if (!TimeValuePattern().IsMatch(value)
                    || !DateTime.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                    throw new JsonException(
                        $"{where} contains token '{span.Value}' whose timestamp is not a full ISO-8601 UTC instant (yyyy-MM-ddTHH:mm:ssZ).");
            }
            else if (!QuantityKinds.Contains(kind))
            {
                throw new JsonException($"{where} contains token '{span.Value}' with unknown quantity kind '{kind}'.");
            }
            else if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                     || !double.IsFinite(parsed))
            {
                throw new JsonException($"{where} contains token '{span.Value}' with an unparsable value.");
            }
        }

        // A stray brace outside any balanced span means a token lost a brace
        // ("Highs near {q:temp:33.5 this afternoon") — BraceSpanPattern cannot
        // see it, so without this check the typo'd token would reach a
        // recipient as literal text, defeating the grammar's whole guarantee.
        var stripped = BraceSpanPattern().Replace(text, " ");
        int strayAt = stripped.IndexOfAny(['{', '}']);
        if (strayAt >= 0)
            throw new JsonException(
                $"{where} contains an unbalanced '{stripped[strayAt]}' (a token is missing its opening or closing brace).");

        var unitAfterToken = UnitAfterTokenPattern().Match(text);
        if (unitAfterToken.Success)
            throw new JsonException(
                $"{where} writes a literal unit next to a token ('{unitAfterToken.Value}'); "
                + "the renderer appends the unit — prose must not.");

        var bareNumberUnit = BareNumberUnitPattern().Match(stripped);
        if (bareNumberUnit.Success)
            throw new JsonException(
                $"{where} writes a unit-bound quantity as plain prose ('{bareNumberUnit.Value.Trim()}'); "
                + "use a {q:...} token, or unit-free wording for quantities with no token kind.");

        return anchors;
    }

    /// <summary>
    /// Visible-prose length of narrative text for the WX-120-style degeneracy
    /// floor: anchors contribute nothing, quantity/time tokens count as a
    /// nominal two characters (a rendered number is never shorter), and
    /// whitespace is collapsed.  Mirrors the spirit of the email_body
    /// visible-text measure — a tokens-only or blank narrative strips to ~0.
    /// </summary>
    public static int VisibleLength(string text)
    {
        var replaced = BraceSpanPattern().Replace(text,
            m => AnchorTokenPattern().IsMatch(m.Value) ? "" : "00");
        return WhitespaceRunPattern().Replace(replaced, " ").Trim().Length;
    }

    /// <summary>Reverse of the serializer's snake_case enum policy: token kind string (<c>temp</c>, <c>precip_mm</c>, …) → <see cref="QuantityKind"/>. The single source the validator's <see cref="QuantityKinds"/> set derives from, read the other direction.</summary>
    private static readonly IReadOnlyDictionary<string, QuantityKind> QuantityKindByToken =
        Enum.GetValues<QuantityKind>().ToDictionary(
            k => JsonNamingPolicy.SnakeCaseLower.ConvertName(k.ToString()), k => k, StringComparer.Ordinal);

    /// <summary>
    /// Substitute every token in <paramref name="text"/> using the SAME grammar
    /// the validator enforces (so the renderer and validator cannot drift): each
    /// <c>{q:&lt;kind&gt;:&lt;value&gt;}</c> quantity is handed to
    /// <paramref name="renderQuantity"/> (with its <see cref="QuantityKind"/> and
    /// canonical-unit value), each <c>{q:time:…}</c> instant is parsed to a UTC
    /// <see cref="DateTime"/> and handed to <paramref name="renderTime"/>, and each
    /// <c>{chN}</c> anchor renders to the empty string.  Intended for bodies that
    /// have already passed <see cref="StructuredReportBody.Validate"/>; a span that
    /// somehow matches no token form is left verbatim rather than thrown on, since
    /// rendering must never crash a send (validation is the gate that rejects
    /// malformed tokens upstream).
    /// </summary>
    public static string Substitute(
        string text,
        Func<QuantityKind, double, string> renderQuantity,
        Func<DateTime, string> renderTime)
    {
        var expanded = BraceSpanPattern().Replace(text, m =>
        {
            var span = m.Value;
            if (AnchorTokenPattern().IsMatch(span))
                return string.Empty;

            var quantity = QuantityTokenPattern().Match(span);
            if (!quantity.Success)
                return span;

            var kind = quantity.Groups["kind"].Value;
            var value = quantity.Groups["value"].Value;

            if (kind == "time")
            {
                return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
                    ? renderTime(instant)
                    : span;
            }

            return QuantityKindByToken.TryGetValue(kind, out var qk)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? renderQuantity(qk, parsed)
                : span;
        });

        // WX-203: when period-ending text — the es time designator "p. m.", or any
        // Spanish abbreviation — immediately precedes a sentence-terminating period,
        // the result is a doubled period ("…tras 9:00 p. m.."). Spanish typography
        // treats the abbreviation's period as also closing the sentence, so collapse
        // an ISOLATED doubled period to one. The lookarounds match only a lone pair,
        // so a run of three or more is left intact — an intentional ellipsis ("..."),
        // or the rarer abbreviation+ellipsis run, which is genuinely ambiguous (drop
        // to three, or a deliberate ellipsis+period keep four?) and so deliberately
        // not guessed at. The es time is the motivating case and the only
        // period-ending TOKEN value, but the rule is general; en's "9:00 PM" has no
        // trailing period, so it never fires there. Applied at the single chokepoint
        // every prose render flows through, so the closing and the change band both get it.
        return DoubledPeriodPattern().Replace(expanded, ".");
    }
}