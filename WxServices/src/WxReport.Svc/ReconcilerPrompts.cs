namespace WxReport.Svc;

/// <summary>
/// Prompt fragments and tool-use schema for the WX-79 forecast reconciliation
/// pass.  The reconciliation guidance text is the second system block in the
/// Claude request, sitting between the recipient persona (block 1) and the
/// per-recipient rendering rules (block 3).  Carrying the
/// <c>cache_control: ephemeral</c> marker, it extends the cached prefix
/// beyond the persona to include the reconciliation procedure itself, so
/// retries and same-recipient cycle reruns within five minutes incur near-
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
    /// Lays out the four inputs Claude receives, the three-step reconciliation
    /// procedure (TAF-vs-GFS by issuance time; observation-overrides-model for
    /// hour X or X+1; prior-snapshot diff for the news judgment), the
    /// significance hierarchy that calibrates "worth sending" (safety-critical /
    /// plans-affecting / ambient-interest tiers, the directional-asymmetry rule,
    /// and worked examples — added in WX-81), and the three artifacts the tool
    /// must return.
    /// </summary>
    internal const string ReconciliationGuidanceText = """
        You receive structured weather data for a single recipient cycle:

          • provisional_snapshot — a ForecastSnapshotBody derived deterministically
            from GFS model output, plus the gfs_model_run_utc. Treat the body as
            your working memory's starting state, covering up to a six-day horizon
            in 6-hour blocks aligned to 00/06/12/18Z.
          • current_observation — the most recent METAR for the station, with its
            observation_time_utc.
          • current_forecast    — the active TAF for the station, with its
            issuance_utc and validity_to_utc; may be null.
          • prior_snapshot      — the most recent committed ForecastSnapshotBody
            for this station, with its generated_at_utc; may be null on a first
            send.
          • changed_since_last_sent_report — which of the three inputs (METAR
            observation, TAF, GFS run) are newer than they were at the last report
            actually DELIVERED to this recipient. "observation only" means no new
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
            of how you choose to describe those winds — let the per-recipient
            rendering rules govern the wording.

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
          • Safety-critical / send. The committed forecast was clear and dry overnight;
            the latest observation and TAF now show dense fog (visibilityExpectation
            poor, obscuration fog). submit_reconciled_report — dense fog is safety-
            critical regardless of how minor the numbers look.
          • Safety-critical / send. Committed winds were 10-18 kt. New guidance brings a
            non-thunderstorm wind event with sustained winds to 36 kt (at or above the
            34 kt line). submit_reconciled_report at any horizon — it crosses tropical-
            storm force.
          • Plans-affecting / skip. The committed forecast already promised a wet day
            (12-18Z block precipExpectation likely, precipPhenomenon rain). A new METAR
            reports light rain in that window. skip_send — the observation merely
            confirms what the recipient was already told; nothing has changed.
          • Plans-affecting / send when near, skip when distant. The committed forecast
            was dry through tomorrow afternoon. A new GFS and TAF bring precipExpectation
            likely, precipPhenomenon rain into tomorrow's 18-00Z block —
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

        You have two tools, and you must call exactly one of them:

          • submit_reconciled_report — when this cycle is worth sending. Return all
            three artifacts (below).
          • skip_send — when this cycle is NOT news worth sending: return only a
            reasoning_trace explaining why no email is warranted. No email is sent
            and the committed forecast is left unchanged.

        The per-recipient instructions state whether skipping is permitted for
        this cycle. Scheduled and first sends are always worth sending — never
        skip them. Only unscheduled, arrival-triggered cycles may be skipped.

        When you DO send, return all three artifacts via the submit_reconciled_report tool:

          • final_snapshot — your refined ForecastSnapshotBody. Same schema as
            provisional_snapshot: schemaVersion 1, ordered 6-hour blocks aligned
            to 00/06/12/18Z, all required fields per block. Temperatures stay in
            Celsius; winds stay in knots; the per-recipient block converts units
            for the email_body.
          • email_body      — HTML matching the rendering rules in the per-recipient
            system block.
          • reasoning_trace — brief audit log naming what changed at each of the
            three reconciliation steps above. Plain English.

        Always act via one of the two tools. Never return free text outside a tool call.
        """;

    /// <summary>
    /// Builds the Anthropic tool definition for the single tool the reconciler
    /// calls.  Required-fields list and enum string values mirror
    /// <see cref="MetarParser.Data.Entities.ForecastSnapshotBlock"/>; the
    /// returned anonymous object serialises directly into the request's
    /// <c>tools</c> array via <see cref="System.Net.Http.Json.JsonContent.Create"/>.
    /// Factory rather than a static field so the anonymous-type instance is
    /// created at the call site (anonymous-type properties carry through to
    /// JSON as written, which is what Anthropic's <c>input_schema</c> needs).
    /// </summary>
    /// <returns>The serialisable tool definition for the reconciler's single tool.</returns>
    internal static object BuildSubmitReconciledReportTool() => new
    {
        name = "submit_reconciled_report",
        description = "Submit the reconciled forecast snapshot, the rendered HTML email body, and the reasoning trace.",
        input_schema = new
        {
            type = "object",
            required = new[] { "email_body", "final_snapshot", "reasoning_trace" },
            properties = new
            {
                email_body = new
                {
                    type = "string",
                    description = "HTML for the email <body> inner content, rendered per the per-recipient system block.",
                },
                final_snapshot = new
                {
                    type = "object",
                    required = new[] { "schemaVersion", "blocks" },
                    properties = new
                    {
                        schemaVersion = new { type = "integer", @const = 1 },
                        blocks = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                required = new[]
                                {
                                    "startUtc", "skyState", "obscuration",
                                    "temperatureCelsius", "windKt", "gustOutlook",
                                    "precipExpectation", "severeFlag", "visibilityExpectation",
                                },
                                properties = new
                                {
                                    startUtc = new { type = "string", format = "date-time" },
                                    skyState = new { type = "string", @enum = new[] { "clear", "partly_cloudy", "mostly_cloudy", "overcast" } },
                                    obscuration = new { type = "string", @enum = new[] { "none", "fog", "haze", "smoke", "dust" } },
                                    temperatureCelsius = new { type = "object", required = new[] { "min", "max" }, properties = new { min = new { type = "number" }, max = new { type = "number" } } },
                                    windKt = new { type = "object", required = new[] { "min", "max" }, properties = new { min = new { type = "integer" }, max = new { type = "integer" } } },
                                    gustOutlook = new { type = "string", @enum = new[] { "none", "occasional", "frequent" } },
                                    precipExpectation = new { type = "string", @enum = new[] { "none", "possible", "likely", "certain" } },
                                    precipPhenomenon = new
                                    {
                                        type = new[] { "string", "null" },
                                        @enum = new object?[] { null, "rain", "thunderstorm", "mixed", "snow", "freezing_precip" },
                                        description = "Required when precipExpectation != 'none'; must be null or omitted when 'none'.",
                                    },
                                    severeFlag = new { type = "boolean" },
                                    visibilityExpectation = new { type = "string", @enum = new[] { "poor", "reduced", "good" } },
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