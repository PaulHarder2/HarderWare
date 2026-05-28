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
    /// hour X or X+1; prior-snapshot diff for the news judgment), and the
    /// three artifacts the tool must return.
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
             the "is this news?" judgment — surface meaningful divergences in
             email_body; suppress trivial drift.

        Return all three artifacts via the submit_reconciled_report tool:

          • final_snapshot — your refined ForecastSnapshotBody. Same schema as
            provisional_snapshot: schemaVersion 1, ordered 6-hour blocks aligned
            to 00/06/12/18Z, all required fields per block. Temperatures stay in
            Celsius; winds stay in knots; the per-recipient block converts units
            for the email_body.
          • email_body      — HTML matching the rendering rules in the per-recipient
            system block.
          • reasoning_trace — brief audit log naming what changed at each of the
            three reconciliation steps above. Plain English.

        Always emit via the tool. Never return free text outside the tool call.
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
                                    startUtc              = new { type = "string", format = "date-time" },
                                    skyState              = new { type = "string", @enum = new[] { "clear", "partly_cloudy", "mostly_cloudy", "overcast" } },
                                    obscuration           = new { type = "string", @enum = new[] { "none", "fog", "haze", "smoke", "dust" } },
                                    temperatureCelsius    = new { type = "object", required = new[] { "min", "max" }, properties = new { min = new { type = "number" }, max = new { type = "number" } } },
                                    windKt                = new { type = "object", required = new[] { "min", "max" }, properties = new { min = new { type = "integer" }, max = new { type = "integer" } } },
                                    gustOutlook           = new { type = "string", @enum = new[] { "none", "occasional", "frequent" } },
                                    precipExpectation     = new { type = "string", @enum = new[] { "none", "possible", "likely", "certain" } },
                                    precipPhenomenon      = new
                                    {
                                        type = new[] { "string", "null" },
                                        @enum = new object?[] { null, "rain", "thunderstorm", "mixed", "snow", "freezing_precip" },
                                        description = "Required when precipExpectation != 'none'; must be null or omitted when 'none'.",
                                    },
                                    severeFlag            = new { type = "boolean" },
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
}