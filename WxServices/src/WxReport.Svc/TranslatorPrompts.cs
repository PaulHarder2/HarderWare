namespace WxReport.Svc;

/// <summary>
/// Prompt fragment and tool-use schema for the WX-172 template-generation pass:
/// one batched Claude call that translates the baseline (en) report vocabulary
/// into a newly-enabled language. Parallels <see cref="ReconcilerPrompts"/> — a
/// stable guidance system block (carrying the <c>cache_control: ephemeral</c>
/// marker so a retry within the cache window is near-free) plus a tool whose
/// <c>input_schema</c> shapes Claude's structured return.
///
/// <para>
/// The renderer assembles <b>atomic tokens</b> (WX-171): grammar-sensitive
/// combinations are single tokens, so most structural mismatch is removed at the
/// source. What remains is the <b>representability self-check</b> — if a token's
/// slot still cannot be filled by a simple word/phrase in the target language
/// (word order, agreement, a restructuring the renderer can't do), Claude must
/// flag it <c>representable=false</c> with a note rather than invent a broken
/// literal. A flagged token leaves the language BLOCKED-needs-code; the renderer
/// assembly is the thing that would need generalizing.
/// </para>
/// </summary>
internal static class TranslatorPrompts
{
    /// <summary>
    /// Stable, language-agnostic guidance block. The specific target language and
    /// the token list travel in the per-call user message, so this block — and the
    /// tool definition — stay byte-identical across languages and retries (cache-friendly).
    /// </summary>
    internal const string TranslationGuidanceText = """
        You translate the fixed vocabulary of a weather-report renderer from English
        into a target language named in the user message. The renderer is fully
        deterministic: it looks each token up by its language-neutral key and
        substitutes your phrase verbatim, with NO further language model involved.
        So every phrase must be correct, idiomatic, and self-contained.

        Each token you receive carries:
          • token        — the language-neutral key; echo it back UNCHANGED.
          • englishPhrase — the phrase as it renders in English. This IS the slot's
            assembly: a plain word/phrase ("light rain"), or a format string with
            {0}, {1}, … placeholders the renderer fills with data ("{0} and {1}").
          • context      — disambiguates the sense (English "at" is Spanish "en" for
            place but "a" for a rate). Its role depends on contextKind.
          • contextKind  — "Example" or "Hint":
              · Example — a sentence USING the token in context. Translate it
                naturally into the target language and return that translation in
                translatedContext (it is an in-context agreement/word-order check on
                your own phrase).
              · Hint — an English usage gloss, NOT a sentence to translate. Read it to
                pick the right sense, and return it UNCHANGED (still in English) in
                translatedContext. A hint stays English in every language.

        Rules for every phrase:
          • Translate the SENSE the context fixes — never a blind word-for-word literal.
          • Preserve EVERY {n} placeholder exactly: same set of numbers, same count,
            none added or dropped. They are data slots; their surrounding words may
            reorder to suit the target grammar, but the placeholders themselves are
            literal. Values, units, and locale are the renderer's job — never add a
            unit, number, or punctuation a placeholder will supply.
          • Keep the meteorological meaning identical to the English. Match its
            register (terse label vs. full clause).

        Representability self-check (the important one):
          • If the target language CANNOT fill this token's slot with a simple
            word/phrase — because its assembly forces a word order, agreement, or
            restructuring a single substituted phrase can't satisfy — set
            representable=false and explain why in note. Leave phrase empty.
          • Do NOT invent a broken or approximate literal to avoid saying "no". A
            false representable=true that ships a malformed phrase is far worse than
            an honest block: a blocked token is recorded and the language is held back
            for a code fix; a bad phrase reaches a reader.
          • When a token IS representable, set representable=true, give the phrase, and
            use note only for a genuine caveat (else null).

        Also return cultureName: the standard IETF BCP-47 culture tag for the target
        language used for date and number formatting (e.g. "fr-FR", "de-DE",
        "pt-BR"). Pick the most widely-applicable region for the language.

        Return everything via the translate_templates tool in one call. Echo back
        EVERY token you were given — exactly the same set, no more, no fewer. Never
        return free text outside the tool call.
        """;

    /// <summary>
    /// Builds the Anthropic tool definition for the single tool the translator calls.
    /// A factory (not a static field) so the anonymous-type instance is created at the
    /// call site, where its properties serialize through to JSON as written — what
    /// Anthropic's <c>input_schema</c> needs.
    /// </summary>
    /// <returns>The serialisable tool definition for <c>translate_templates</c>.</returns>
    internal static object BuildTranslateTemplatesTool() => new
    {
        name = "translate_templates",
        description = "Return the localized phrase for every report-vocabulary token, "
            + "with a representability self-check per token and the language's culture tag.",
        input_schema = new
        {
            type = "object",
            required = new[] { "cultureName", "translations" },
            properties = new
            {
                cultureName = new
                {
                    type = "string",
                    description = "IETF BCP-47 culture tag for the target language's date/number formatting (e.g. \"fr-FR\").",
                },
                translations = new
                {
                    type = "array",
                    description = "One entry per token supplied in the user message — the same set, no extras, none dropped.",
                    items = new
                    {
                        type = "object",
                        required = new[] { "token", "phrase", "translatedContext", "representable" },
                        properties = new
                        {
                            token = new { type = "string", description = "The language-neutral key, echoed back unchanged." },
                            phrase = new
                            {
                                type = "string",
                                description = "The localized phrase, with every {n} placeholder preserved. Empty ONLY when representable is false.",
                            },
                            translatedContext = new
                            {
                                type = "string",
                                description = "An Example context translated into the target language; a Hint context returned unchanged in English.",
                            },
                            representable = new
                            {
                                type = "boolean",
                                description = "False when no simple word/phrase can fill this token's slot in the target language (word order / agreement / restructuring) — leave phrase empty and explain in note.",
                            },
                            note = new
                            {
                                type = new[] { "string", "null" },
                                description = "Why the token is not representable, or a genuine translation caveat; null when there is nothing to note.",
                            },
                        },
                    },
                },
            },
        },
    };
}