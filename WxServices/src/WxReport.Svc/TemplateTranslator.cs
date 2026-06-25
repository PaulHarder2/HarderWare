using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using MetarParser.Data.Entities;

using WxServices.Logging;

namespace WxReport.Svc;

/// <summary>
/// One token's translation as Claude returned it, after fail-closed validation
/// (WX-172).  A <see cref="Representable"/>=false entry is a self-flagged
/// BLOCKED-needs-code token: it carries a <see cref="Note"/> explaining why no
/// simple phrase fills the slot and an empty <see cref="Phrase"/>, and it leaves
/// the language not-ready.
/// </summary>
/// <param name="Token">The language-neutral key, echoed from the baseline.</param>
/// <param name="Phrase">The localized phrase (empty when not representable).</param>
/// <param name="TranslatedContext">The translated example sentence, or the unchanged English hint, for the row's <see cref="LanguageTemplate.ContextInfo"/>.</param>
/// <param name="Representable">Whether a simple phrase can fill the slot in this language.</param>
/// <param name="Note">Why the token is not representable, or a translation caveat; null when there is nothing to note.</param>
public sealed record TranslatedToken(
    string Token, string Phrase, string TranslatedContext, bool Representable, string? Note);

/// <summary>
/// Outcome of a single language's template generation (WX-172).  A
/// <see cref="Success"/> means Claude returned a well-formed, fully-validated set
/// covering every baseline token — though some entries may be self-flagged
/// not-representable (the caller turns those into the BLOCKED state).  A
/// <see cref="Failure"/> is a transport/parse/exhausted-validation fault: the
/// caller leaves the language PENDING so the next cycle retries (the FAILED state).
/// </summary>
public abstract record TranslateResult
{
    private TranslateResult() { }

    /// <summary>Generation produced a validated translation for every baseline token.</summary>
    /// <param name="Translations">One entry per baseline token; some may be not-representable.</param>
    /// <param name="CultureName">IETF culture tag Claude returned for date/number formatting.</param>
    /// <param name="Usage">Token usage summed across attempts.</param>
    public sealed record Success(
        IReadOnlyList<TranslatedToken> Translations, string CultureName, TokenUsage Usage) : TranslateResult;

    /// <summary>Generation failed (transport, truncation, or malformed output after retries); retry next cycle.</summary>
    /// <param name="Reason">Short human-readable description of the failure.</param>
    /// <param name="Usage">Token usage summed across attempts (a failed attempt is still billed).</param>
    public sealed record Failure(string Reason, TokenUsage Usage) : TranslateResult;
}

/// <summary>
/// Orchestrates the WX-172 generation-on-enable pass: turns the baseline (en)
/// report vocabulary into one newly-enabled language via a single batched Claude
/// tool-use call, validates the response fail-closed, and returns either a
/// validated <see cref="TranslateResult.Success"/> (which may carry
/// not-representable tokens) or a typed <see cref="TranslateResult.Failure"/>.
/// Parallels <see cref="ForecastReconciler"/> — the same retry-with-feedback loop
/// (a rejected attempt is replayed as tool_use + tool_result so the retry corrects
/// the specific fault) and the same fail-closed posture: a malformed return is
/// rejected and never persisted, so a broken phrase can't reach a reader.
///
/// <para>
/// Deterministic validation (the output-integrity middle layer): the returned set
/// must cover exactly the baseline token set; every representable phrase must be
/// non-empty, preserve its baseline's <c>{n}</c> placeholders (as a multiset —
/// reordering for grammar is fine, adding/dropping is not), fit the column bound,
/// and be control-char-free; every not-representable token must carry a note.
/// </para>
/// </summary>
public sealed class TemplateTranslator
{
    // Column bounds from WeatherDataContext.OnModelCreating (LanguageTemplate / Language).
    // A return over a bound is malformed output (rejected), not a silent truncation.
    private const int PhraseMaxLength = 500;
    private const int ContextMaxLength = 1000;
    private const int NoteMaxLength = 1000;
    private const int CultureMaxLength = 20;

    private const int MaxAttempts = 3;

    // All brace-delimited placeholders ({0}, {1}, …) — data slots the renderer fills.
    // The translation may reorder them to suit the target grammar but must neither add
    // nor drop one, so they're compared as a multiset.
    private static readonly Regex Placeholder = new(@"\{[^}]*\}", RegexOptions.Compiled);

    private readonly ClaudeClient _claude;

    /// <summary>Initializes a new instance backed by the supplied <see cref="ClaudeClient"/>.</summary>
    /// <param name="claude">Anthropic Messages API wrapper used for the translate_templates tool-use call.</param>
    public TemplateTranslator(ClaudeClient claude) => _claude = claude;

    /// <summary>
    /// Generates the localized templates for <paramref name="targetDisplayName"/> from the
    /// baseline rows, retrying up to three attempts on malformed output (each retry replays
    /// the rejected attempt with its error so Claude corrects the specific fault).  Returns
    /// a validated <see cref="TranslateResult.Success"/> — possibly with not-representable
    /// tokens — or a <see cref="TranslateResult.Failure"/> on transport/truncation/exhaustion.
    /// </summary>
    /// <param name="targetDisplayName">English display name of the target language (e.g. "French"), named to Claude.</param>
    /// <param name="targetIso">Target ISO code, for logging only.</param>
    /// <param name="baselineRows">The baseline (en) representable templates — the source set whose tokens define completeness.</param>
    /// <param name="ct">Cancellation token propagated to the underlying HTTP call.</param>
    /// <returns>A validated success or a typed failure.</returns>
    /// <sideeffects>Makes one or more HTTP POSTs to the Anthropic Messages API. Writes log entries on validation failure.</sideeffects>
    public async Task<TranslateResult> GenerateAsync(
        string targetDisplayName,
        string targetIso,
        IReadOnlyList<LanguageTemplate> baselineRows,
        CancellationToken ct = default)
    {
        var baseline = baselineRows.Where(r => r.Representable).ToList();
        if (baseline.Count == 0)
            return new TranslateResult.Failure(
                "No baseline (en) templates to translate from — the seed/migration is missing.",
                new TokenUsage(0, 0, 0, 0));

        var baselineByToken = baseline.ToDictionary(r => r.Token, StringComparer.Ordinal);
        var userMessage = BuildUserMessage(targetDisplayName, targetIso, baseline);

        int accIn = 0, accOut = 0, accCacheRead = 0, accCacheWrite = 0;
        var corrections = new List<ReconciliationCorrection>();

        for (int attempt = 1; ; attempt++)
        {
            var api = await _claude.InvokeTranslationAsync(userMessage, corrections, ct);
            if (api is null)
                return new TranslateResult.Failure(
                    "Claude API call failed or returned no translate_templates tool_use block.",
                    new TokenUsage(accIn, accOut, accCacheRead, accCacheWrite));

            accIn += api.Tokens.InputTokens;
            accOut += api.Tokens.OutputTokens;
            accCacheRead += api.Tokens.CacheReadInputTokens;
            accCacheWrite += api.Tokens.CacheCreationInputTokens;
            var usage = new TokenUsage(accIn, accOut, accCacheRead, accCacheWrite);

            // A max_tokens truncation yields a partial translations array — re-calling at
            // the same cap would just re-truncate, so fail (retry next cycle), like the
            // reconciler. The cap is already generous (see ClaudeClient.TranslationOutputTokens).
            if (api.StopReason == "max_tokens")
            {
                Logger.Error($"Template generation for '{targetIso}' was truncated at the output-token cap (stop_reason=max_tokens).");
                return new TranslateResult.Failure(
                    "Translation response was truncated at the output-token cap (stop_reason=max_tokens).", usage);
            }

            try
            {
                var (cultureName, translations) = Validate(api.ToolUseInput, baselineByToken);
                int blocked = translations.Count(t => !t.Representable);
                Logger.Info($"Template generation for '{targetIso}' produced {translations.Count} token(s)"
                    + (blocked > 0 ? $", {blocked} not representable (BLOCKED)" : "") + $" (culture {cultureName}).");
                return new TranslateResult.Success(translations, cultureName, usage);
            }
            catch (JsonException ex)
            {
                if (attempt < MaxAttempts)
                {
                    // Replay this rejected attempt with the reason so the retry corrects the
                    // specific fault rather than blindly resampling (WX-148 pattern).
                    corrections.Add(new ReconciliationCorrection(
                        api.ToolUseId, api.ToolName, api.ToolUseInput,
                        $"Your previous translate_templates response was rejected: {ex.Message} "
                        + "Fix only that and resubmit the full set via the tool."));
                    Logger.Warn($"Template generation for '{targetIso}' failed validation (attempt {attempt}/{MaxAttempts}): {ex.Message}; retrying with feedback.");
                    continue;
                }
                Logger.Error($"Template generation for '{targetIso}' failed validation after {MaxAttempts} attempts: {ex.Message}");
                return new TranslateResult.Failure($"Malformed translation after {MaxAttempts} attempts: {ex.Message}", usage);
            }
        }
    }

    // Builds the user message: the target language named, then the JSON token list.
    private static string BuildUserMessage(string targetDisplayName, string targetIso, IReadOnlyList<LanguageTemplate> baseline)
    {
        var items = baseline
            .OrderBy(r => r.Token, StringComparer.Ordinal)   // stable order → cache-friendly prefix
            .Select(r => new
            {
                token = r.Token,
                englishPhrase = r.Phrase,
                context = r.ContextInfo,
                contextKind = r.ContextKind.ToString(),   // "Example" | "Hint"
            });
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = false });
        var sb = new StringBuilder();
        sb.Append("Target language: ").Append(targetDisplayName).Append(" (").Append(targetIso).Append(").\n\n");
        sb.Append("Translate every token below into the target language and return one entry per token ");
        sb.Append("via the translate_templates tool. Echo each token key unchanged.\n\n");
        sb.Append(json);
        return sb.ToString();
    }

    // Fail-closed validation. Throws JsonException (routed through the retry loop) on any
    // malformed return; returns (cultureName, translations) on success. A not-representable
    // token is NOT a failure — it's a valid, expected outcome that the caller turns into BLOCKED.
    internal static (string CultureName, IReadOnlyList<TranslatedToken> Translations) Validate(
        JsonElement input, IReadOnlyDictionary<string, LanguageTemplate> baselineByToken)
    {
        var cultureName = RequireString(input, "cultureName").Trim();
        if (cultureName.Length == 0 || cultureName.Length > CultureMaxLength)
            throw new JsonException($"cultureName '{cultureName}' is empty or exceeds {CultureMaxLength} characters.");

        if (!input.TryGetProperty("translations", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new JsonException("missing or non-array 'translations'.");

        var result = new List<TranslatedToken>(baselineByToken.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in arr.EnumerateArray())
        {
            var token = RequireString(item, "token");
            if (!baselineByToken.TryGetValue(token, out var baseRow))
                throw new JsonException($"translations contains an unknown token '{token}' not in the baseline set.");
            if (!seen.Add(token))
                throw new JsonException($"translations contains duplicate token '{token}'.");

            bool representable = RequireBool(item, "representable");
            string phrase = RequireString(item, "phrase");
            string translatedContext = RequireString(item, "translatedContext");
            string? note = OptionalString(item, "note");

            // Checks that apply to BOTH paths: translatedContext and note are persisted
            // regardless of representability (a BLOCKED row's context + note are shown in
            // the operator UI), so they get the same fail-closed bounds and char hygiene
            // either way — reject, never silently truncate.
            if (translatedContext.Length > ContextMaxLength)
                throw new JsonException($"token '{token}' translatedContext exceeds {ContextMaxLength} characters.");
            if (note is { Length: > NoteMaxLength })
                throw new JsonException($"token '{token}' note exceeds {NoteMaxLength} characters.");
            if (HasControlChar(translatedContext) || (note is not null && HasControlChar(note)))
                throw new JsonException($"token '{token}' context or note contains a control character.");

            if (!representable)
            {
                // BLOCKED token: must explain why, so the operator sees what needs a code fix.
                if (string.IsNullOrWhiteSpace(note))
                    throw new JsonException($"token '{token}' is not representable but carries no explanatory note.");
                result.Add(new TranslatedToken(token, "", translatedContext, false, note));
                continue;
            }

            // Representable: enforce a usable phrase.
            if (string.IsNullOrWhiteSpace(phrase))
                throw new JsonException($"token '{token}' is representable but its phrase is empty.");
            if (phrase.Length > PhraseMaxLength)
                throw new JsonException($"token '{token}' phrase exceeds {PhraseMaxLength} characters.");
            if (HasControlChar(phrase))
                throw new JsonException($"token '{token}' phrase contains a control character.");
            if (!PlaceholdersPreserved(baseRow.Phrase, phrase))
                throw new JsonException(
                    $"token '{token}' phrase does not preserve the baseline placeholders "
                    + $"(baseline '{baseRow.Phrase}', got '{phrase}'); every {{n}} slot must appear exactly once.");

            result.Add(new TranslatedToken(token, phrase, translatedContext, true, note));
        }

        // Exact-set match: every baseline token must be present (a missing one is an
        // unrenderable token), and no extra (already rejected above as "unknown token").
        if (seen.Count != baselineByToken.Count)
        {
            var missing = baselineByToken.Keys.Where(t => !seen.Contains(t)).OrderBy(t => t, StringComparer.Ordinal);
            throw new JsonException($"translations is missing baseline token(s): {string.Join(", ", missing)}.");
        }

        return (cultureName, result);
    }

    // Order-insensitive, count-sensitive equality of the {…} placeholders in two strings.
    private static bool PlaceholdersPreserved(string englishPhrase, string translatedPhrase)
    {
        static List<string> Tokens(string s) =>
            Placeholder.Matches(s).Select(m => m.Value).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var a = Tokens(englishPhrase);
        var b = Tokens(translatedPhrase);
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    // A report-vocabulary phrase / example sentence is single-line text; ANY control
    // character (including newline and tab) is malformed output — reject it. A stray NUL
    // can truncate at the DB driver, and a newline can break a heading/subject line.
    private static bool HasControlChar(string s)
    {
        foreach (var ch in s)
            if (char.IsControl(ch))
                return true;
        return false;
    }

    // ── tool_use field accessors (mirror ForecastReconciler) ───────────────────

    private static string RequireString(JsonElement input, string field) =>
        input.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s
            ? s
            : throw new JsonException($"missing or non-string required field '{field}'.");

    private static bool RequireBool(JsonElement input, string field) =>
        input.TryGetProperty(field, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : throw new JsonException($"missing or non-boolean required field '{field}'.");

    private static string? OptionalString(JsonElement input, string field) =>
        input.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}