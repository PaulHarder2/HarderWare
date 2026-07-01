using System.Text.Json;

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// Prompt fragments and tool-use schema for the WX-79 forecast reconciliation
/// pass, run once per locality (WX-130).  The reconciliation guidance text is
/// the second system block in the Claude request, sitting between the persona
/// (block 1) and the per-locality cycle instructions (block 3); the HTML
/// rendering rules are gone — a deterministic renderer (WX-129) now builds each
/// recipient's email from the structured report.  Carrying the
/// <c>cache_control: ephemeral</c> marker, it extends the cached prefix
/// beyond the persona to include the reconciliation procedure itself, so
/// retries and same-locality cycle reruns within five minutes incur near-
/// zero token cost for these blocks.
///
/// <para>
/// The tool schema mirrors <see cref="MetarParser.Data.Entities.ForecastSnapshotBody"/>
/// so Claude's tool_use response round-trips through
/// <see cref="MetarParser.Data.Entities.ForecastSnapshotBody.Deserialize"/> for
/// both shape validation and the precipPhenomenon-iff-non-none invariant.
/// Enum string values use the same <c>snake_case_lower</c> form produced by
/// the body's <c>JsonStringEnumConverter</c> policy.
/// </para>
/// </summary>
internal static class ReconcilerPrompts
{
    /// <summary>
    /// Stable, recipient-agnostic system block added to every reconciler call.
    /// Lays out the five inputs Claude receives, the three-step reconciliation
    /// procedure (TAF-vs-GFS by issuance time; observation-overrides-model for
    /// hour X or X+1; prior-snapshot diff for the news judgment), the
    /// significance hierarchy that calibrates "worth sending" (safety-critical /
    /// plans-affecting / ambient-interest tiers, the directional-asymmetry rule,
    /// and worked examples — added in WX-81), and the three artifacts the tool
    /// must return.
    /// </summary>
    internal const string ReconciliationGuidanceText = """
        You receive structured weather data for a single locality cycle:

          • provisional_snapshot — a ForecastSnapshotBody derived deterministically
            from GFS model output, plus the gfs_model_run_utc. Treat the body as
            your working memory's starting state, covering up to a six-day horizon
            in blocks aligned to the locality's local clock-band boundaries (00/06/12/18
            local time: 00-06 / 06-12 / 12-18 / 18-24). Each block's startUtc is
            the UTC instant of its local clock-band boundary.
          • current_observation — the most recent METAR for the station, with its
            observation_time_utc.
          • current_forecast    — the active TAF for the station, with its
            issuance_utc and validity_to_utc; may be null.
          • prior_snapshot      — the most recent committed ForecastSnapshotBody
            for this station, with its generated_at_utc; may be null on a first
            send.
          • changed_since_last_sent_report — which of the three inputs (METAR
            observation, TAF, GFS run) are newer than they were at the last report
            actually SENT for this locality. "observation only" means no new
            TAF or GFS run has arrived since you last sent — the only fresh input is
            a routine hourly observation.

        Reconciliation procedure:

          1. Compare provisional_snapshot against current_forecast. For block start
             times within the TAF's validity window, prefer the TAF where it diverges
             from the provisional ONLY IF the TAF's issuance_utc is later than the
             gfs_model_run_utc. If the GFS run is newer than the TAF issuance, the
             GFS reflects fresher data and prevails.

          2. Compare against current_observation. An observation at hour X overrides
             the provisional for hour X or hour X+1 only; beyond that, the model
             projection prevails. Adjust the earliest block(s) accordingly.

          3. Compare against prior_snapshot, if present. The diff is the basis for
             the "is this news?" judgment. If the new evidence is genuinely worth
             telling the recipient about, produce a report. If it merely confirms
             or trivially drifts from what the prior_snapshot already committed —
             e.g. observed weather the prior forecast already predicted — it is
             NOT news.

        Significance hierarchy:

        Not every invalidation is equally worth a recipient's attention. Weigh the
        news against three tiers, and against how near the affected blocks are.

          • Safety-critical — severe thunderstorms (damaging winds, large hail, or
            tornadic potential), dense fog, ice or freezing precipitation, and newly
            forecast sustained non-thunderstorm winds of 34 kt (tropical-storm force)
            or greater. Any newly introduced or newly removed hazard at this tier is
            news at ANY horizon and warrants a prompt send; when in doubt here, send.
            An ordinary, non-severe thunderstorm is NOT automatically safety-critical:
            a change to scattered thunderstorms reaches this tier only when the storm
            energy points to strong-to-severe storms (the snapshot's severeFlag and
            convective signals). Otherwise treat thunderstorms as plans-affecting.
            When winds of 34 kt or greater qualify, the send is warranted regardless
            of how you describe those winds in the narrative. Two wind rules for the
            structured snapshot: windKt is SUSTAINED wind only (min/max) — NEVER fold a
            gust into windKt.max; a gust belongs solely in the narrative {q:gust} token.
            The one exception is severity: a wind of 50 kt or more, sustained OR gust, is
            severe by definition — set severeFlag true on that block.

          • Plans-affecting — precipitation versus dry, notable but non-hazardous
            winds, a meaningful temperature swing, ordinary non-severe thunderstorms.
            Invalidations in the near horizon (today or tomorrow) are news worth an
            unscheduled send. The same shift several days out is not urgent — let it
            ride the next scheduled send rather than firing an update now.

          • Ambient-interest — pleasant-day signals, light breezes, minor sky changes
            with no bearing on plans or safety. These belong in scheduled sends only.
            Never fire an unscheduled update for an ambient-interest change alone.

        Directional asymmetry: the appearance of weather you did not promise is almost
        always more newsworthy than the non-appearance of weather you did promise.

          • "Rain or a storm arrived when the committed forecast said dry" — news,
            promptly. The recipient was told to expect one thing and is getting another.
          • "No rain yet, though the committed forecast said rainy" — NOT news at
            first. A forecast of rain today is not invalidated the moment it isn't yet
            raining. Treat the non-arrival as news only once the promised window has
            clearly and substantially passed without the weather materializing.

        Stability and anti-reversal: severe potential and precipitation-likelihood
        upgrades are driven by model and TAF guidance, not by a single new
        observation. When changed_since_last_sent_report is "observation only" — no
        newer GFS run or TAF since your last sent report — do NOT reverse, re-open,
        or re-escalate a severeFlag or precip tier you already committed in
        prior_snapshot; keep them consistent. A new observation can still be news
        when it shows weather ARRIVING that the prior forecast did not promise (the
        directional asymmetry above), but it is never a reason to flip a forecast
        judgment the model has not changed, and it is never a reason to re-send what
        prior_snapshot already told the recipient just because you re-derived it
        slightly differently this cycle.

        Worked examples (tier — situation — decision):

          • Safety-critical / send. The committed 18-00Z block was precipExpectation
            likely, precipPhenomenon rain, severeFlag false. A new TAF plus convective
            signals now point to severe storms (damaging gusts, large hail) — set
            precipPhenomenon thunderstorm, severeFlag true, and submit_reconciled_report
            at any horizon. The upgrade to severe is itself the news, even though rain
            was already expected.
          • Safety-critical / send. Committed winds were 10-18 kt. New guidance brings a
            non-thunderstorm wind event with sustained winds to 36 kt (at or above the
            34 kt line). submit_reconciled_report at any horizon — it crosses tropical-
            storm force.
          • Plans-affecting / skip. The committed forecast already promised a wet day
            (the afternoon block precipExpectation likely, precipPhenomenon rain). A new METAR
            reports light rain in that window. skip_send — the observation merely
            confirms what the recipient was already told; nothing has changed.
          • Plans-affecting / send when near, skip when distant. The committed forecast
            was dry through tomorrow afternoon. A new GFS and TAF bring precipExpectation
            likely, precipPhenomenon rain into tomorrow's evening block —
            submit_reconciled_report, because rain is newly appearing where dry was
            promised inside the plans horizon. The identical dry-to-rain shift five days
            out is not urgent — skip_send and let it ride the next scheduled send.
          • Plans-affecting / skip. The committed forecast several days out was
            precipPhenomenon rain. A new GFS shifts it to scattered, non-severe
            thunderstorms (severeFlag false, modest energy) at that distant horizon.
            skip_send — garden-variety storms days away are not urgent and can ride the
            next scheduled send.
          • Ambient-interest / skip. Committed winds were 5-10 kt; new data nudges them
            to 8-12 kt, a pleasant breeze with no hazard and no plans impact. skip_send
            — a few knots of non-hazardous drift is ambient and can wait.
          • Ambient-interest / skip. The committed sky was partly_cloudy; new data
            shifts an afternoon block to mostly_cloudy with no precipitation and no
            other change. skip_send — a minor sky-cover change with no bearing on plans
            or safety is ambient-interest only.
          • Plans-affecting / skip (fresh TAF, no material change). A new TAF was just
            issued, but reconciled against prior_snapshot it changes nothing a reader
            would act on — same sky, same precipExpectation and precipPhenomenon, winds
            within a few knots. skip_send — a fresh TAF issuance is not itself news;
            only a material change to the committed forecast is.
          • Safety-critical / skip (anti-reversal on observation only). The committed
            18-00Z block was severeFlag true from the latest GFS and TAF.
            changed_since_last_sent_report is "observation only" — no newer model run or
            TAF. Re-reading the numbers you might now call the afternoon merely
            "scattered storms," but nothing fresh supports lowering the hazard. Keep
            severeFlag true and skip_send — do not whipsaw the recipient by reversing a
            severe call on a routine observation alone.

        Decision rule (arrival-triggered cycles, where skip_send is offered) —
        default to skip_send. Call submit_reconciled_report ONLY when your reconciled
        final_snapshot differs from prior_snapshot in a way a reader would act on:

          • a change in sky or precipitation CATEGORY (not a small
            numeric wobble within the same category);
          • a wind change that crosses into a higher impact band — under 17, 17–33,
            34–47, 48–63, or 64+ kt (≤ half tropical-storm, up to TS/gale, storm,
            hurricane force). Drift within one band (e.g. 7 kt to 15 kt) is NOT news;
          • a temperature swing of more than a few degrees;
          • any hazard newly appearing or clearing (per the significance hierarchy).

        A newer TAF or GFS issuance is NOT itself news. If reconciling the fresher
        guidance leaves the committed forecast materially unchanged — same categories,
        same wind band, temperatures within a couple of degrees — call skip_send even
        though the inputs advanced. An unscheduled email must earn its interruption;
        when the only differences are sub-categorical wobble, skip. (Safety-critical
        changes always clear this bar — when a genuine hazard appears, send.)

        You have two tools, and you must call exactly one of them:

          • submit_reconciled_report — when this cycle is worth sending. Return all
            three artifacts (below).
          • skip_send — when this cycle is NOT news worth sending: return only a
            reasoning_trace explaining why no email is warranted. No email is sent
            and the committed forecast is left unchanged.

        The per-cycle instructions state whether skipping is permitted for
        this cycle. Scheduled and first sends are always worth sending — never
        skip them. Only unscheduled, arrival-triggered cycles may be skipped.

        When you DO send, return all three artifacts via the submit_reconciled_report tool:

          • final_snapshot — your refined ForecastSnapshotBody. Same schema as
            provisional_snapshot: schemaVersion 5, ordered blocks aligned
            to the locality's local clock-band boundaries (00/06/12/18 local time:
            00-06 / 06-12 / 12-18 / 18-24), all required fields per block.
            Temperatures stay in Celsius; winds stay in knots — a deterministic
            renderer converts units per recipient.
          • structured_report — the unit-neutral structured report. It is the
            ONLY rendered artifact (there is no email_body): a deterministic
            program turns it into each recipient's report, applying their units
            and language with no further LLM involvement. Rules below.
          • reasoning_trace — brief audit log naming what changed at each of the
            three reconciliation steps above. Plain English.

        structured_report rules:

          The structured report is rendered to each recipient's email by a
          deterministic program with NO further LLM involvement, so it must be
          unit-neutral and language-complete by construction.

          • You do NOT author a structured change list. After this call, a
            deterministic program computes "what changed" by comparing
            prior_snapshot against your final_snapshot, so you cannot introduce a
            structural change the data does not support — get the final_snapshot
            right and the change set follows. Your job for the band is the
            changeSummary PROSE (below).
              - In changeSummary, describe in one or two plain sentences what
                genuinely differs from prior_snapshot: precipitation appearing
                where the prior was dry, a band strengthening or weakening, a
                hazard appearing or clearing, a meaningful temperature or wind
                change. Narrate only REAL prior-vs-now differences the
                final_snapshot supports — if the prior already carried the same
                precipitation at the same likelihood in a window, it has NOT
                changed; do not narrate it, and never describe an onset, downgrade,
                or clearing the comparison does not show.
              - Sky-cover drift (partly/mostly cloudy/overcast) and a few knots of
                wind within the same impact band are NOT news — do not narrate
                them in the change band.
              - Never describe precipitation, a storm, or a hazard at a time the
                final_snapshot blocks do not carry it; the per-day grid the reader
                sees is built from those same blocks, so the band must agree with
                them. Express every instant as a {q:time:...} token (the renderer
                shows it in local time), never an internal clock window.
          • narrative — one entry per language code requested below, each with
            exactly two prose sections: changeSummary (the change-band prose; null
            only on a scheduled or diagnostic report with no near-term severe
            onset) and closing (the "In summary:" wrap-up). The current-
            conditions table and the per-day forecast grid are rendered
            deterministically from the data, so do NOT narrate them here. Write
            each language natively and idiomatically — never translate
            word-for-word — but keep the meteorological content identical across
            languages. When an "Approved vocabulary for this report" glossary is
            provided below, use its approved wording for each listed concept — you
            may inflect it for grammatical agreement (gender/number) and adjust its
            capitalization to fit the word's position in the sentence, but do not
            replace it with a synonym — and compose the sentence naturally around it.
            The probability words — possible, likely, and expected — name distinct
            forecast-confidence tiers and are NOT interchangeable: render the tier
            the source states with that tier's own approved word, never a higher- or
            lower-confidence one, and never use an "is expected" / "is forecast" verb
            construction (e.g. Spanish "se espera") in place of the anchored word for
            "likely" or "possible".
          • Quantity tokens: inside narrative prose, NEVER write a number with a
            unit. Write a token the renderer substitutes in the recipient's own
            units and locale:
              {q:temp:33.5}    temperature, degrees Celsius
              {q:temp_range:24:26}  a temperature RANGE, two °C endpoints
                               (low:high); renders as one converted span
              {q:wind:22}      sustained wind, knots
              {q:gust:30}      gust, knots
              {q:pressure:1013.2}  pressure, hPa
              {q:precip_mm:12} liquid-equivalent accumulation, millimetres
              {q:time:2026-06-08T21:00:00Z}  an instant, ISO-8601 UTC; rendered
                               in the locality's local time
            Values use a period decimal separator regardless of language.
          • The renderer appends the unit when it substitutes a token. NEVER
            write a unit name or symbol next to a token, in any language — not
            "gusts to {q:gust:30} kt", not "{q:temp:33.5} degrees", not
            "ráfagas de {q:gust:30} nudos". Write "gusts to {q:gust:30}" and
            the recipient sees "gusts to 35 mph" or "ráfagas de 56 km/h" per
            their preferences.
          • The same rule for quantities WITHOUT a token kind (visibility,
            distance): never write a bare number with a unit ("visibility is
            1.5 miles") — the renderer cannot convert plain prose. Use
            unit-free wording instead ("visibility is sharply reduced",
            "dense fog nearby"). Unitless figures like a humidity percentage
            ("88%") are fine.
          • Unit-neutral phrasing: never use phrasing that only works in one
            unit system — no "in the low 90s", no "below freezing point of 32".
            Say "highs near {q:temp:33.5}", "gusts to {q:gust:30}". Relative or
            unit-free phrasing ("a sharp warm-up", "near freezing") is fine.
          • Daily high/low summary: when you summarize daytime highs or overnight
            lows in the closing, use the ready ranges given under
            temperature_summary in the per-cycle data, written as their
            {q:temp_range:lo:hi} tokens — verbatim. NEVER build a band by wrapping a
            single {q:temp:...} point in vague words ("the upper {q:temp:36} range",
            "highs in the low {q:temp:33}s"): a {q:temp_range:...} token already
            renders a complete range in the recipient's units. You still phrase the
            sentence (and any early/later split) natively and idiomatically.
          • Never write raw UTC block notation in prose. The 6-hour grid
            shorthand ("12-18Z", "18Z", "the 12-18Z block") is internal
            engineering notation; it must NEVER reach the reader. Express every
            instant as a {q:time:...} token, which the renderer shows in the
            recipient's own local time.
          • Never name an internal data source or use aviation jargon in prose —
            no "TAF", "METAR", "GFS", "CAPE", "ICAO"; the reader doesn't know them.
            Refer to the underlying data vaguely: "the latest indications", "the
            short-term outlook". Do NOT say "the latest forecast" (that is the
            report itself) or "guidance".
          • Prose time-of-day words must match the LOCAL time their {q:time}
            token renders to. The renderer converts each token to the locality's
            local clock, so a token at 12:00Z that is 7:00 AM locally reads as
            "morning", not "afternoon". When you write a day-part word
            ("morning", "afternoon", "evening") beside a {q:time} token, make the
            word agree with that token's local hour.
          • The 00:00-06:00 pre-dawn block has NO safe day-part word: "overnight"
            and "{weekday} night" both float to the WRONG calendar day for a US
            reader, who hears "Saturday night" as the night that FOLLOWS Saturday,
            not its first six hours. Never use them for this block. Bind it to its
            day explicitly and without a night-word — e.g. "the early hours of
            Saturday" or "Saturday, shortly after midnight" — beside the {q:time}
            token, which renders the exact local time.
          • Each block is exactly one local day-part (WX-155): its local start hour
            names it — 06:00 morning, 12:00 afternoon, 18:00 evening, and 00:00 the
            pre-dawn block (bound to its day per the rule above, never "overnight").
            A change window covers whole blocks, so name its day-part from the
            block(s) it spans; that name will agree with the {q:time} rule above.
          • Hedged certainty: never state weather as flatly certain, in any
            language — no forecast is ever 100% sure. Render even a "certain"
            precip expectation or a set severeFlag as calibrated strong
            likelihood ("almost certain", "highly likely", "expect"), never as a
            guarantee ("will", "definitely", "guaranteed").
          • The closing only SUMMARIZES the reconciled forecast (the current
            conditions, the per-day grid, and the change band). It must NOT
            introduce a precipitation, storm, or hazard chance — or a timing for
            one — that the final_snapshot blocks do not carry. If the blocks show
            a dry evening, do not write "a chance of a storm tonight"; if a storm
            sits only in an afternoon block, do not move it to "tonight". Speak
            only of weather the blocks support, at the time they place it. Saying
            a period stays dry or quiet is always fine.

        Always act via one of the two tools. Never return free text outside a tool call.
        """;

    /// <summary>
    /// Builds the Anthropic tool definition for the single tool the reconciler
    /// calls.  Required-fields list and enum string values mirror
    /// <see cref="MetarParser.Data.Entities.ForecastSnapshotBlock"/> and
    /// <see cref="MetarParser.Data.Entities.StructuredReportBody"/>; the
    /// returned anonymous object serialises directly into the request's
    /// <c>tools</c> array via <see cref="System.Net.Http.Json.JsonContent.Create"/>.
    /// Factory rather than a static field so the anonymous-type instance is
    /// created at the call site (anonymous-type properties carry through to
    /// JSON as written, which is what Anthropic's <c>input_schema</c> needs).
    /// </summary>
    /// <param name="narrativeLanguages">
    /// ISO 639-1 codes of the languages the structured report's narrative must
    /// carry — the distinct languages of the locality's recipients (WX-128).
    /// Each becomes a required key of the <c>narrative</c> schema object, so a
    /// missing language fails Anthropic-side schema pressure as well as our own
    /// fail-closed validation.  Stable per recipient/locality, so the tool
    /// definition — part of the cached prompt prefix — stays byte-identical
    /// across that recipient's cycles and retries.
    /// </param>
    /// <returns>The serialisable tool definition for the reconciler's single tool.</returns>
    internal static object BuildSubmitReconciledReportTool(IReadOnlyList<string> narrativeLanguages)
    {
        var narrativeSectionSchema = new
        {
            type = "object",
            required = new[] { "closing" },
            properties = new
            {
                changeSummary = new
                {
                    type = new[] { "string", "null" },
                    description = "Prose for the change band: one or two plain sentences describing what genuinely changed since the prior committed forecast. Null only on a scheduled/diagnostic report with no near-term severe onset. Quantities appear only as {q:...} tokens; no anchors.",
                },
                closing = new { type = "string", description = "Prose for the \"In summary:\" closing. The current-conditions table and per-day grid are rendered deterministically — narrate only the change band and this closing." },
            },
        };
        // Normalized: distinct + ordinal-sorted, so the serialized tool JSON —
        // part of the cached prompt prefix, which renders ahead of the system
        // blocks — is byte-identical for a given language SET regardless of
        // caller ordering. An unordered set from a future WX-130 DB query must
        // not silently split the Anthropic prompt cache (WX-128 review finding).
        var languages = narrativeLanguages
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var narrativeProperties = languages.ToDictionary(
            lang => lang, _ => (object)narrativeSectionSchema);

        return new
        {
            name = "submit_reconciled_report",
            description = "Submit the reconciled forecast snapshot, the unit-neutral structured report, and the reasoning trace.",
            input_schema = new
            {
                type = "object",
                required = new[] { "final_snapshot", "structured_report", "reasoning_trace" },
                properties = new
                {
                    structured_report = new
                    {
                        type = "object",
                        // WX-189: Claude no longer authors the structural change set. It
                        // returns only the narrative prose; the deterministic
                        // DeterministicChangeDetector computes changes[] from
                        // (prior_snapshot, final_snapshot) after the call, so a phantom
                        // structural change is impossible by construction.
                        required = new[] { "schemaVersion", "narrative" },
                        properties = new
                        {
                            schemaVersion = new { type = "integer", @const = StructuredReportBody.SchemaVersionCurrent },
                            narrative = new
                            {
                                type = "object",
                                description = "Narrative prose per ISO 639-1 language code; every listed language is required and no other key is permitted. Quantities appear only as {q:...} tokens.",
                                required = languages,
                                properties = narrativeProperties,
                                additionalProperties = false,
                            },
                        },
                    },
                    final_snapshot = new
                    {
                        type = "object",
                        required = new[] { "schemaVersion", "blocks" },
                        properties = new
                        {
                            schemaVersion = new { type = "integer", @const = ForecastSnapshotBody.SchemaVersionCurrent },
                            blocks = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    required = new[]
                                    {
                                        "startUtc", "skyState", "obscuration",
                                        "temperatureCelsius", "windKt",
                                        "precipExpectation", "severeFlag",
                                    },
                                    properties = new
                                    {
                                        startUtc = new { type = "string", format = "date-time" },
                                        skyState = new { type = "string", @enum = new[] { "clear", "partly_cloudy", "mostly_cloudy", "overcast" } },
                                        obscuration = new { type = "string", @enum = new[] { "none", "fog", "haze", "smoke", "dust" } },
                                        temperatureCelsius = new { type = "object", required = new[] { "min", "max" }, properties = new { min = new { type = "number" }, max = new { type = "number" } } },
                                        windKt = new { type = "object", required = new[] { "min", "max" }, description = "Min/max SUSTAINED wind in knots. NEVER put a gust here: max is the sustained-wind ceiling, not the gust. A gust appears only in the narrative {q:gust} token. (A gust still matters for severeFlag — see the wind-severe rule.)", properties = new { min = new { type = "integer" }, max = new { type = "integer" } } },
                                        precipExpectation = new { type = "string", @enum = new[] { "none", "possible", "likely", "certain" } },
                                        precipPhenomenon = new
                                        {
                                            type = new[] { "string", "null" },
                                            @enum = new object?[] { null, "rain", "thunderstorm", "mixed", "snow", "freezing_precip" },
                                            description = "Required when precipExpectation != 'none'; must be null or omitted when 'none'.",
                                        },
                                        severeFlag = new { type = "boolean" },
                                    },
                                },
                            },
                        },
                    },
                    reasoning_trace = new
                    {
                        type = "string",
                        description = "Brief audit log naming what changed at each of the three reconciliation steps.",
                    },
                },
            },
        };
    }

    /// <summary>
    /// Builds the Anthropic tool definition for the WX-80 invalidation gate's
    /// "not news" outcome.  Offered alongside
    /// <see cref="BuildSubmitReconciledReportTool"/> only on unscheduled,
    /// arrival-triggered cycles (where skipping is permitted); Claude calls it
    /// when the new evidence is not worth emailing.  It returns just a
    /// reasoning_trace — no email, no snapshot — and the caller suppresses the
    /// send while leaving the committed forecast unchanged.
    /// </summary>
    /// <returns>The serialisable tool definition for the skip-send decision.</returns>
    internal static object BuildSkipSendTool() => new
    {
        name = "skip_send",
        description = "Use when the new evidence is NOT news worth emailing the recipient — "
            + "e.g. it confirms or only trivially drifts from the prior committed forecast. "
            + "No email is sent and the committed forecast is left unchanged.",
        input_schema = new
        {
            type = "object",
            required = new[] { "reasoning_trace" },
            properties = new
            {
                reasoning_trace = new
                {
                    type = "string",
                    description = "Brief plain-English explanation of why this cycle's evidence is not news worth sending.",
                },
            },
        },
    };
}