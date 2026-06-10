using System.Net;
using System.Text;
using System.Text.Json;

using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// Drives ForecastReconciler against a stubbed Anthropic Messages API endpoint.
// Each test wires a StubHandler that returns predetermined JSON; the reconciler
// posts, parses, validates, and either lands in ReconcileResult.Success with
// the parsed THREE artifacts (final_snapshot, structured_report, reasoning_trace)
// or in ReconcileResult.Failure with a reason. WX-130: the email_body artifact is
// gone (a deterministic renderer builds each recipient's email from the structured
// report), and the structured report is the LIVE, fatal artifact again — a
// validation failure on it routes through the retry → skip/Failure path, exactly
// like a final_snapshot schema violation.
// The HTTP-failure test deliberately returns a 400-class status so the
// ClaudeClient retry loop (which retries only 429/529/5xx) exits immediately.

public class ForecastReconcilerTests
{
    // A valid WX-128/130 structured_report whose 'en' narrative clears the
    // per-language degeneracy floor (MinVisibleNarrativeChars). The narrative
    // carries only the two judgment sections (changeSummary + closing); the
    // current-conditions table and per-day grid are rendered deterministically.
    private const string ValidStructuredReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": {
              "changeSummary": null,
              "closing": "A wet stretch ahead — keep an umbrella handy through the weekend."
            }
          }
        }
        """;

    // WX-148: a structured report announcing rain appearing in a block-aligned
    // 11–17Z window (the {ch1} anchor appears in the changeSummary, as the body
    // contract requires when changes is non-empty).
    private const string RainAppearingAlignedReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Rain is now likely.", "closing": "A wet stretch ahead — keep an umbrella handy." }
          }
        }
        """;

    // The same change but with an OFF-GRID window (17–21Z) — the 6/9 contradiction shape.
    private const string RainAppearingOffGridReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-09T17:00:00Z", "endUtc": "2026-06-09T21:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Rain is now likely this afternoon.", "closing": "A wet stretch ahead — keep an umbrella handy." }
          }
        }
        """;

    // A final_snapshot whose 11–17Z block carries rain — backs the appearing-rain change.
    private const string RainBlockSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":22,"max":30},"windKt":{"min":5,"max":12},"precipExpectation":"possible","precipPhenomenon":"rain","severeFlag":false}]}
        """;

    // Schema-valid (closing is non-blank) but below the per-language visible floor:
    // the WX-120 fall-safe degeneracy case the reconciler turns into a skip/Failure.
    private const string DegenerateStructuredReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": { "changeSummary": null, "closing": "ok" }
          }
        }
        """;

    // ── WX-149 prose-hygiene fixtures ─────────────────────────────────────────

    // Defect 1 (send 1938): a change typed "thunderstorm" appearing, but the only
    // precip block carries rain. The WX-148 phenomenon-backing rule already rejects
    // this phantom (the block must carry the named phenomenon) — WX-149 pins it.
    private const string PhantomThunderstormSafetyReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "safety", "phenomenon": "thunderstorm", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Storms are now possible this morning.", "closing": "Keep an eye on the sky." }
          }
        }
        """;

    // Defect 4 (send 1938): "possible rain" emitted at the safety tier. The rain
    // block backs the phenomenon (so the WX-148 check passes), but no block carries
    // a safety-grade signal — WX-149's tier-backing assertion rejects it.
    private const string SafetyRainOverEscalationReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "safety", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Rain is now possible this morning.", "closing": "Keep an umbrella handy." }
          }
        }
        """;

    // Defect 3 (send 1938): raw internal block notation leaked into the closing.
    private const string RawUtcLeakReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": { "changeSummary": null, "closing": "The Wednesday afternoon block (12-18Z) has shifted from dry to wet." }
          }
        }
        """;

    // A lower-cased leak ("12-18z") must be caught too (case-insensitive, PR #87).
    private const string RawUtcLeakLowercaseReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": { "changeSummary": null, "closing": "The afternoon block (12-18z) has shifted from dry to wet." }
          }
        }
        """;

    // Defect 2 (send 1927): prose says "afternoon" next to a {q:time} token at
    // 12:00Z, which renders to 7:00 AM local (CDT) — the morning, not afternoon.
    private const string ProseTimeMismatchReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-13T11:00:00Z", "endUtc": "2026-06-13T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Rain is now expected to develop Saturday afternoon, {q:time:2026-06-13T11:00:00Z}.", "closing": "A wet start to the weekend." }
          }
        }
        """;

    // The same shape but the prose word AGREES with the token's local rendering
    // (12:00Z is 7:00 AM CDT — morning) — must NOT be rejected.
    private const string ProseTimeAgreesReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Rain develops this morning, {q:time:2026-06-09T11:00:00Z}.", "closing": "A wet start to the day." }
          }
        }
        """;

    // A safety-tier thunderstorm BACKED by a severe block — must NOT be rejected.
    private const string SafetyThunderstormBackedReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "safety", "phenomenon": "thunderstorm", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Severe storms are now likely.", "closing": "Stay weather-aware today." }
          }
        }
        """;

    // A safety-tier FOG change — fog has no snapshot field, so it is exempt from
    // the tier-backing check (WX-149 documented residual) and must NOT be rejected.
    private const string FogSafetyExemptReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "safety", "phenomenon": "fog", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Dense fog is now likely.", "closing": "Allow extra time on the roads." }
          }
        }
        """;

    // A 6/13 11-17Z block carrying rain — backs the prose-token-mismatch change.
    private const string RainBlock613SnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-13T11:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":22,"max":30},"windKt":{"min":5,"max":12},"precipExpectation":"possible","precipPhenomenon":"rain","severeFlag":false}]}
        """;

    // A 6/9 11-17Z block carrying a severe thunderstorm — backs a safety-tier change.
    private const string SevereThunderstormBlockSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":10,"max":20},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":true}]}
        """;

    // ── WX-151 prior-aware snapshots ──────────────────────────────────────────

    // A 6/9 11-17Z block carrying rain at LIKELY (RainBlockSnapshotJson is the same
    // block at possible) — a stronger prior, for weakening (Likely→Possible).
    private const string RainLikelyBlockSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":22,"max":30},"windKt":{"min":5,"max":12},"precipExpectation":"likely","precipPhenomenon":"rain","severeFlag":false}]}
        """;

    // A 6/9 11-17Z block carrying freezing precip (possible) — a prior whose hazard
    // the new (rain) snapshot legitimately clears.
    private const string FreezingPrecipBlockSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":-1,"max":2},"windKt":{"min":5,"max":12},"precipExpectation":"possible","precipPhenomenon":"freezingPrecip","severeFlag":false}]}
        """;

    // A 6/9 11-17Z thunderstorm POSSIBLE, severeFlag FALSE — prior for the severe-
    // escalation guard (expectation flat, severe rises false→true).
    private const string StormPossibleNoSevereSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":10,"max":20},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false}]}
        """;

    // A 6/9 11-17Z thunderstorm POSSIBLE, severeFlag TRUE — new snapshot for the
    // severe-escalation guard, and for severe-appearing.
    private const string StormPossibleSevereSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":10,"max":20},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":true}]}
        """;

    // ── WX-151 prior-aware change fixtures (window 6/9 11-17Z) ─────────────────
    // changeSummaries avoid day-part words so the anchored-prose check stays inert
    // (these fixtures exercise the prior-aware verification, not the prose check).

    private const string RainWeakeningReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "weakening", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}The rain is easing off.", "closing": "Drier than it looked." }
          }
        }
        """;

    private const string StormStrengtheningReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "thunderstorm", "direction": "strengthening", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}The storm threat has stepped up.", "closing": "Worth keeping an eye on." }
          }
        }
        """;

    private const string SevereAppearingReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "safety", "phenomenon": "severe", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Severe weather is now in the forecast.", "closing": "Stay weather-aware." }
          }
        }
        """;

    // A genuine change but the prose times it "this evening" while the window
    // (6/9 11-17Z) is morning in CDT — the anchored-prose contradiction.
    private const string RainAppearingEveningProseReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Rain develops this evening.", "closing": "Keep an umbrella handy." }
          }
        }
        """;

    // Same window, but the anchored sentence names an in-window part plus a
    // transition word ("this morning … by evening") — must NOT be rejected.
    private const string RainAppearingTransitionProseReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Rain develops this morning, tapering by evening.", "closing": "A damp start." }
          }
        }
        """;

    // A safety-tier hazard CLEARING — the new snapshot correctly no longer carries
    // a safety signal (the block is plain rain). A "newly removed hazard" is still
    // safety-tier news, so the tier-backing check must NOT fire on a removal
    // direction. (Snapshot: RainBlockSnapshotJson — rain, no severe/freezing.)
    private const string SafetyClearingReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "safety", "phenomenon": "freezingPrecip", "direction": "clearing", "window": { "startUtc": "2026-06-09T11:00:00Z", "endUtc": "2026-06-09T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}The ice threat has lifted; roads should be clear.", "closing": "A safer commute than this morning looked." }
          }
        }
        """;

    // A compound sentence whose day-part word ("evening") belongs to a DIFFERENT
    // time reference than the {q:time} token (8:00 AM CDT — morning). Separated by
    // intervening words, so it must NOT be read as governing the token.
    private const string CompoundSentenceProseTimeReportJson = """
        {
          "schemaVersion": 5,
          "changes": [
            { "tier": "plans", "phenomenon": "rain", "direction": "appearing", "window": { "startUtc": "2026-06-13T11:00:00Z", "endUtc": "2026-06-13T17:00:00Z" }, "quantities": [], "summaryToken": "ch1" }
          ],
          "narrative": {
            "en": { "changeSummary": "{ch1}Showers taper Friday evening, then redevelop {q:time:2026-06-13T13:00:00Z} Saturday.", "closing": "An unsettled couple of days." }
          }
        }
        """;

    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_ReturnsSuccess_WithThreeArtifactsAndTokens()
    {
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "All three steps clean.",
            inputTokens: 100, outputTokens: 50,
            cacheReadInputTokens: 80, cacheCreationInputTokens: 10);

        var result = await RunReconciler(responseJson);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal("All three steps clean.", success.ReasoningTrace);
        Assert.Empty(success.FinalSnapshot.Blocks);
        Assert.True(success.StructuredReport.Narrative.ContainsKey("en"));
        Assert.Empty(success.StructuredReport.Changes);
        Assert.Equal(100, success.Tokens.InputTokens);
        Assert.Equal(50, success.Tokens.OutputTokens);
        Assert.Equal(80, success.Tokens.CacheReadInputTokens);
        Assert.Equal(10, success.Tokens.CacheCreationInputTokens);
    }

    // ── WX-130 structured_report contract — now the LIVE, fatal artifact ───────
    // The structured report is the rendering source, so a validation failure on it
    // is fatal (retry → skip/Failure), not best-effort as it was during the WX-144
    // additive transition.

    [Fact]
    public async Task StructuredReportMissing_IsFatal_ReturnsFailure()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("structured_report", failure.Reason);
    }

    [Fact]
    public async Task StructuredReportMissingRequestedLanguage_Degrades_AfterRetries()
    {
        // The cycle requests en AND es; the narrative is internally valid but carries
        // en only. The per-call contract failure is retried (bounded); since the
        // final_snapshot itself parsed cleanly, the exhausted result DEGRADES (WX-148)
        // rather than failing outright — the snapshot is still usable for a hazard
        // report, only the suspect narrative is dropped.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }, narrativeLanguages: new[] { "en", "es" });

        var degraded = Assert.IsType<ReconcileResult.Degraded>(result);
        Assert.Contains("missing requested language", degraded.Reason);
        Assert.Equal(3, calls); // retried (bounded) like any malformed structured artifact
    }

    [Fact]
    public async Task StructuredReportWithExtraLanguage_Degrades_AfterRetries()
    {
        // Exact-set contract: an unrequested language is unvalidated content for no
        // recipient — retried, then DEGRADES (WX-148) because the final_snapshot parsed.
        var withExtra = ValidStructuredReportJson.Replace(
            "\"narrative\": {",
            "\"narrative\": { \"es\": { \"changeSummary\": null, \"closing\": \"Un cierre razonable y suficientemente largo.\" },");
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: withExtra);

        var result = await RunReconciler(responseJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(result);
        Assert.Contains("unrequested language", degraded.Reason);
    }

    [Fact]
    public async Task DegenerateNarrative_WhenGuaranteedSend_ReturnsFailure()
    {
        // Schema-valid but near-blank narrative (below the per-language floor): on a
        // guaranteed send it cannot become a skip — it fails closed so the
        // provisional stays and the next cycle self-heals (WX-120 carried forward).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: DegenerateStructuredReportJson);

        var result = await RunReconciler(responseJson, allowSkip: false);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("content-less narrative", failure.Reason);
    }

    [Fact]
    public async Task DegenerateNarrative_WhenAllowSkip_RetriesThenSkips()
    {
        // A near-blank narrative on a skippable cycle matches Claude's (skip-leaning)
        // reasoning: after bounded retries it becomes a NotNews and keeps the trace.
        var degenerate = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "Confirms the prior forecast — not news. SKIP.",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: DegenerateStructuredReportJson);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(degenerate, Encoding.UTF8, "application/json"),
            };
        }, allowSkip: true);

        var notNews = Assert.IsType<ReconcileResult.NotNews>(result);
        Assert.Equal("Confirms the prior forecast — not news. SKIP.", notNews.ReasoningTrace);
        Assert.Equal(3, calls); // bounded at maxAttempts
    }

    [Fact]
    public async Task FreshFinalSnapshotWithStaleVersion_FailsClosed()
    {
        // Deserialize stays lenient for old persisted priors, but a FRESH
        // reconciled snapshot must carry the current version — Claude copying
        // the prior's older digit fails closed (WX-128 review).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":2,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("not the current version", failure.Reason);
    }

    // ── invalidation gate: skip_send ────────────────────────────────────────

    [Fact]
    public async Task SkipSend_WhenAllowed_ReturnsNotNews_WithTraceAndTokens()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput(
            """{ "reasoning_trace": "Observed rain matches the prior forecast — not news." }""",
            inputTokens: 70, outputTokens: 12,
            cacheReadInputTokens: 60, cacheCreationInputTokens: 0,
            toolName: "skip_send");

        var result = await RunReconciler(responseJson, allowSkip: true);

        var notNews = Assert.IsType<ReconcileResult.NotNews>(result);
        Assert.Equal("Observed rain matches the prior forecast — not news.", notNews.ReasoningTrace);
        Assert.Equal(70, notNews.Tokens.InputTokens);
        Assert.Equal(12, notNews.Tokens.OutputTokens);
        Assert.Equal(60, notNews.Tokens.CacheReadInputTokens);
    }

    [Fact]
    public async Task SkipSend_WhenNotAllowed_ReturnsFailure_NotNotNews()
    {
        // A guaranteed send (scheduled / first / startup) passes allowSkip:false
        // and forces submit_reconciled_report. If Claude nonetheless returns
        // skip_send, the send must never be silently suppressed — the result must
        // be Failure, not NotNews.
        //
        // WX-80: enforcement now lives primarily at the API boundary — ClaudeClient
        // rejects the un-offered skip_send (returns null) before it reaches the
        // reconciler, which then surfaces a Failure. The reconciler's own
        // !allowSkip guard remains as documented defense in depth. Either path
        // yields the same end-to-end contract this test pins, so we assert the
        // behavior (Failure, not NotNews) rather than a specific reason string.
        var responseJson = BuildClaudeResponseJsonWithRawInput(
            """{ "reasoning_trace": "trying to skip a guaranteed send" }""",
            toolName: "skip_send");

        var result = await RunReconciler(responseJson, allowSkip: false);

        Assert.IsType<ReconcileResult.Failure>(result); // never silently a NotNews
    }

    [Fact]
    public async Task SkipSend_MissingReasoningTrace_ReturnsFailure()
    {
        // skip_send with no reasoning_trace fails — naming the missing field —
        // rather than silently suppressing a send with no recorded rationale.
        var responseJson = BuildClaudeResponseJsonWithRawInput(
            """{ }""",
            toolName: "skip_send");

        var result = await RunReconciler(responseJson, allowSkip: true);

        // WX-104: a missing key is reported by name, not mislabelled "schema validation failed".
        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("reasoning_trace", failure.Reason);
        Assert.DoesNotContain("Schema validation failed", failure.Reason);
    }

    // ── failure: transport ──────────────────────────────────────────────────

    [Fact]
    public async Task HttpStatus400_ReturnsFailure()
    {
        // 400 is outside the retry-on (429/529/5xx) window, so the
        // ClaudeClient retry loop exits on the first attempt — keeps the
        // test fast without sleeping through the backoff.
        var result = await RunReconciler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"type\":\"invalid_request_error\"}}"),
            });

        Assert.IsType<ReconcileResult.Failure>(result);
    }

    // ── failure: no tool_use block ──────────────────────────────────────────

    [Fact]
    public async Task ResponseWithoutToolUseBlock_ReturnsFailure()
    {
        // Claude responded with text only — no tool_use block.  Should hit
        // the "no submit_reconciled_report tool_use block" branch.
        var responseJson = """
            {
              "id": "msg_x",
              "type": "message",
              "role": "assistant",
              "content": [
                { "type": "text", "text": "Sorry, I cannot reconcile right now." }
              ],
              "model": "claude-sonnet-4-6",
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 10, "output_tokens": 10 }
            }
            """;

        var result = await RunReconciler(responseJson);

        Assert.IsType<ReconcileResult.Failure>(result);
    }

    // ── failure: tool input missing final_snapshot ──────────────────────────

    [Fact]
    public async Task ToolInputMissingFinalSnapshot_ReturnsFailure()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        // WX-104: names the missing field; no KeyNotFoundException, no "schema validation failed".
        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("final_snapshot", failure.Reason);
        Assert.DoesNotContain("Schema validation failed", failure.Reason);
    }

    // ── failure: tool input missing reasoning_trace ─────────────────────────

    [Fact]
    public async Task ToolInputMissingReasoningTrace_ReturnsFailure()
    {
        // final_snapshot is present and valid so the failure isolates the missing
        // reasoning_trace (the reconciler checks final_snapshot, then reasoning_trace).
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "structured_report": {{ValidStructuredReportJson}}
            }
            """);

        var result = await RunReconciler(responseJson);

        // WX-104: names the missing field; no KeyNotFoundException, no "schema validation failed".
        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("reasoning_trace", failure.Reason);
        Assert.DoesNotContain("Schema validation failed", failure.Reason);
    }

    // ── failure: response truncated at the output-token cap (WX-109) ─────────

    [Fact]
    public async Task ResponseTruncatedAtTokenCap_ReturnsTruncationFailure_NotMissingField()
    {
        // stop_reason "max_tokens" means generation was cut at the output-token
        // cap, so the tool_use input is a truncated partial object with a trailing
        // required field dropped. The reconciler must report truncation, not
        // mislabel it a missing field — it keys on stop_reason, before reading fields.
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "reasoning_trace": "trace"
            }
            """,
            stopReason: "max_tokens");

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("truncated", failure.Reason);
        Assert.Contains("max_tokens", failure.Reason);
        Assert.DoesNotContain("missing required field", failure.Reason);
    }

    [Fact]
    public async Task ResponseTruncatedAtTokenCap_DetectedEvenWhenPartialInputLooksComplete()
    {
        // Defense-in-depth: a "max_tokens" stop_reason is authoritative. Even if the
        // truncated partial input happens to still carry all three artifacts, we do
        // not trust a capped generation — the result is the truncation Failure.
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """,
            stopReason: "max_tokens");

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("truncated", failure.Reason);
    }

    // ── WX-110: bounded retry on a retryable-malformed (non-truncation) response ─

    [Fact]
    public async Task RetryableMalformed_ThenValid_RetriesAndSucceeds()
    {
        // First response omits structured_report (a complete, non-truncated tool_use
        // that simply dropped the advisory-required field); the second is complete.
        // The reconciler retries within the cycle and lands on Success.
        var malformed = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """);
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            var body = calls == 1 ? malformed : valid;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, calls); // exactly one retry was needed
    }

    [Fact]
    public async Task RetryableMalformed_ThenValid_SumsTokensAcrossAttempts()
    {
        // The failed attempt is real billable spend; the returned Tokens must include
        // it so the cost dashboards don't undercount retried cycles.
        var malformed = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """); // default tokens: 10 in / 10 out / 0 / 0
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 100, outputTokens: 50, cacheReadInputTokens: 80, cacheCreationInputTokens: 10);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            var body = calls == 1 ? malformed : valid;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(110, success.Tokens.InputTokens);          // 10 + 100
        Assert.Equal(60, success.Tokens.OutputTokens);           // 10 + 50
        Assert.Equal(80, success.Tokens.CacheReadInputTokens);   // 0 + 80
        Assert.Equal(10, success.Tokens.CacheCreationInputTokens); // 0 + 10
    }

    [Fact]
    public async Task DegenerateNarrative_ThenValid_RetriesAndSucceeds()
    {
        // A near-blank narrative first response recovers on retry to a real report:
        // Success, exactly one retry.
        var degenerate = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: DegenerateStructuredReportJson);
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            var body = calls == 1 ? degenerate : valid;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, calls); // one retry to recover
    }

    [Fact]
    public async Task RetryableMalformed_AllAttemptsFail_ReturnsFailureBoundedAtThreeCalls()
    {
        var malformed = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(malformed, Encoding.UTF8, "application/json"),
            };
        });

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("structured_report", failure.Reason);
        Assert.Equal(3, calls); // bounded at maxAttempts — no runaway retry
    }

    [Fact]
    public async Task MaxTokensTruncation_IsNotRetried()
    {
        // A max_tokens stop_reason is authoritative: re-calling at the same cap would
        // just re-truncate, so the truncation Failure is returned on the first call.
        var truncated = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """, stopReason: "max_tokens");

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(truncated, Encoding.UTF8, "application/json"),
            };
        });

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("truncated", failure.Reason);
        Assert.Equal(1, calls); // not retried
    }

    // ── failure: garbage final_snapshot JSON ────────────────────────────────

    [Fact]
    public async Task FinalSnapshotIsGarbageString_ReturnsFailure()
    {
        // final_snapshot is a string where ForecastSnapshotBody expects an
        // object — JsonElement.GetRawText returns the quoted string, which
        // ForecastSnapshotBody.Deserialize will fail on.
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": "this is not a snapshot body",
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── failure: precipPhenomenon-iff-non-none invariant ────────────────────

    [Fact]
    public async Task FinalSnapshotViolatesPrecipPhenomenonInvariant_ReturnsFailure()
    {
        // Block has precipExpectation = "none" but precipPhenomenon = "rain".
        // ForecastSnapshotBody.Validate enforces the invariant on deserialize.
        var blockJson = """
            {
              "startUtc": "2026-05-28T00:00:00Z",
              "skyState": "clear",
              "obscuration": "none",
              "temperatureCelsius": { "min": 10.0, "max": 20.0 },
              "windKt": { "min": 5, "max": 10 },
              "precipExpectation": "none",
              "precipPhenomenon": "rain",
              "severeFlag": false
            }
            """;
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [{{blockJson}}] },
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── WX-148 change ↔ snapshot consistency validator ───────────────────────

    [Fact]
    public async Task ChangeWindowOffGrid_IsRejected_ThenDegrades()
    {
        // The 6/9 contradiction shape: a 17–21Z window is off the 6-hour block grid,
        // so the deterministic day-grid (built from the blocks) and the narrative would
        // disagree. The validator rejects it; retries exhaust; the clean snapshot degrades.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingOffGridReportJson);

        var result = await RunReconciler(responseJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(result);
        Assert.Contains("aligned", degraded.Reason);
    }

    [Fact]
    public async Task AppearingPrecip_BackedByBlock_Succeeds()
    {
        // A block-aligned 11–17Z window whose appearing rain is carried by the 11Z
        // block passes the validator cleanly.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingAlignedReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Single(success.StructuredReport.Changes);
    }

    [Fact]
    public async Task AppearingPrecip_NotBackedByAnyBlock_Degrades()
    {
        // A block-aligned window, but the snapshot has no block carrying the announced
        // rain — the narrative would promise precip the grid never shows. Rejected,
        // retried, then degraded on the (clean) empty snapshot.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingAlignedReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(await RunReconciler(responseJson));
        Assert.Contains("did not occur", degraded.Reason);
    }

    // ── WX-149 prose-hygiene validator assertions ────────────────────────────

    [Fact]
    public async Task PhantomPhenomenon_NotCarriedByAnyBlock_Degrades()
    {
        // Defect 1 (Watonga 6/10, send 1938): a change typed "thunderstorm" over a
        // day whose only precip block is rain. The phenomenon-backing rule rejects
        // the phantom; retries exhaust; the clean snapshot degrades.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: PhantomThunderstormSafetyReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(await RunReconciler(responseJson));
        Assert.Contains("did not occur", degraded.Reason);
    }

    [Fact]
    public async Task SafetyTier_NotBackedBySevereSignal_Degrades()
    {
        // Defect 4 (Watonga 6/10, send 1938): "possible rain" at the safety tier.
        // The rain block backs the phenomenon, but no block carries a safety-grade
        // signal — the tier-backing assertion rejects the over-escalation.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: SafetyRainOverEscalationReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(await RunReconciler(responseJson));
        Assert.Contains("over-escalated", degraded.Reason);
    }

    [Fact]
    public async Task RawUtcBlockNotation_InProse_Degrades()
    {
        // Defect 3 (Watonga 6/10, send 1938): internal "(12-18Z)" shorthand leaked
        // into the reader-facing closing. Rejected; retries exhaust; snapshot degrades.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RawUtcLeakReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(await RunReconciler(responseJson));
        Assert.Contains("raw UTC block notation", degraded.Reason);
    }

    [Fact]
    public async Task RawUtcBlockNotation_LowerCase_InProse_Degrades()
    {
        // Case-insensitive: a lower-cased "12-18z" leak is caught too (PR #87).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RawUtcLeakLowercaseReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(await RunReconciler(responseJson));
        Assert.Contains("raw UTC block notation", degraded.Reason);
    }

    [Fact]
    public async Task ProseTimeWord_ContradictsTokenLocalRendering_Degrades()
    {
        // Defect 2 (Spring 6/13, send 1927): prose says "afternoon" beside a
        // {q:time} token at 12:00Z that renders to 7:00 AM local (CDT) — morning.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlock613SnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseTimeMismatchReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Contains("contradicts", degraded.Reason);
    }

    [Fact]
    public async Task ProseTimeWord_AgreesWithTokenLocalRendering_Succeeds()
    {
        // The conservative check must not false-reject: "morning" beside a token
        // that renders to 7:00 AM local (CDT) agrees, so the report sends cleanly.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseTimeAgreesReportJson);

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task SafetyTier_BackedBySevereBlock_Succeeds()
    {
        // A genuine safety call — thunderstorm appearing, backed by a severeFlag
        // block carrying it — passes both the phenomenon- and tier-backing checks.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SevereThunderstormBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: SafetyThunderstormBackedReportJson);

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
    }

    [Fact]
    public async Task SafetyTier_FogPhenomenon_ExemptFromBackingCheck_Succeeds()
    {
        // Fog has no snapshot field, so a safety-tier fog change is exempt from the
        // tier-backing check (documented residual) and must not be false-rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: FogSafetyExemptReportJson);

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
    }

    [Fact]
    public async Task SafetyTier_HazardClearing_NotBacked_Succeeds()
    {
        // A removal-direction safety change ("the ice threat has lifted"): the new
        // snapshot legitimately carries no safety signal, but a newly removed hazard
        // is still safety-tier news — the tier-backing check must not false-reject it.
        // The prior carried freezing precip in-window, so the prior-aware check sees
        // a real clearing (WX-151).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: SafetyClearingReportJson);

        Assert.IsType<ReconcileResult.Success>(
            await RunReconciler(responseJson, prior: PriorOf(FreezingPrecipBlockSnapshotJson)));
    }

    [Fact]
    public async Task ProseTimeWord_BoundToDifferentReference_NotRejected_Succeeds()
    {
        // "Friday evening" modifies a different instant than the morning {q:time}
        // token; intervening words separate them, so it must not be read as a
        // contradiction (guards the conservative proximity rule against false rejects).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlock613SnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: CompoundSentenceProseTimeReportJson);

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    // ── WX-151 prior-aware change verification ────────────────────────────────

    private const string DryPriorSnapshotJson = """{"schemaVersion":5,"blocks":[]}""";

    [Fact]
    public async Task PriorAware_PrecipUnchanged_PhantomAppearing_Degrades()
    {
        // The send-1977 repro essence: prior and new carry the SAME rain in-window,
        // so "rain appearing" is a change that did not occur. Rejected → degrade.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingAlignedReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(
            await RunReconciler(responseJson, prior: PriorOf(RainBlockSnapshotJson)));
        Assert.Contains("did not occur", degraded.Reason);
    }

    [Fact]
    public async Task PriorAware_DryToRain_GenuineAppearing_Succeeds()
    {
        // Prior was dry in-window, new carries rain — a real onset, not a phantom.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingAlignedReportJson);

        Assert.IsType<ReconcileResult.Success>(
            await RunReconciler(responseJson, prior: PriorOf(DryPriorSnapshotJson)));
    }

    [Fact]
    public async Task PriorAware_LikelyToPossible_Weakening_Succeeds()
    {
        // Prior rain Likely, new rain Possible — a real weakening.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainWeakeningReportJson);

        Assert.IsType<ReconcileResult.Success>(
            await RunReconciler(responseJson, prior: PriorOf(RainLikelyBlockSnapshotJson)));
    }

    [Fact]
    public async Task PriorAware_RainUnchanged_PhantomWeakening_Degrades()
    {
        // Prior fully covers the window and carries the SAME rain as new, so a
        // "rain weakening" is a change that did not occur — rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainWeakeningReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(
            await RunReconciler(responseJson, prior: PriorOf(RainBlockSnapshotJson)));
        Assert.Contains("did not occur", degraded.Reason);
    }

    [Fact]
    public async Task PriorAware_PhantomStrengthening_PhenomenonAbsentBothSides_Degrades()
    {
        // "thunderstorm strengthening" over a window that carries only rain in both
        // prior and new — the storm exists nowhere, so the change is phantom.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: StormStrengtheningReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(
            await RunReconciler(responseJson, prior: PriorOf(RainBlockSnapshotJson)));
        Assert.Contains("did not occur", degraded.Reason);
    }

    [Fact]
    public async Task PriorAware_SevereEscalation_FlatExpectation_NotFalseRejected_Succeeds()
    {
        // WX-148 worked example: thunderstorm expectation flat (Possible→Possible) but
        // severeFlag rises false→true. That IS a real strengthening — must not be
        // rejected just because the expectation band did not move.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: StormPossibleSevereSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: StormStrengtheningReportJson);

        Assert.IsType<ReconcileResult.Success>(
            await RunReconciler(responseJson, prior: PriorOf(StormPossibleNoSevereSnapshotJson)));
    }

    [Fact]
    public async Task PriorAware_SeverePhenomenon_GenuineAppearing_Succeeds()
    {
        // severeFlag false→true in-window backs a "severe appearing" change.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: StormPossibleSevereSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: SevereAppearingReportJson);

        Assert.IsType<ReconcileResult.Success>(
            await RunReconciler(responseJson, prior: PriorOf(StormPossibleNoSevereSnapshotJson)));
    }

    [Fact]
    public async Task PriorAware_SeverePhenomenon_AlreadySevere_PhantomAppearing_Degrades()
    {
        // Prior already severe in-window — "severe appearing" did not happen.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: StormPossibleSevereSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: SevereAppearingReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(
            await RunReconciler(responseJson, prior: PriorOf(StormPossibleSevereSnapshotJson)));
        Assert.Contains("did not occur", degraded.Reason);
    }

    [Fact]
    public async Task PriorAware_Weakening_NullPrior_NotRejected_Succeeds()
    {
        // First send (no prior): weakening/clearing can't be verified — there's
        // nothing to have weakened from — so it must NOT be hard-rejected (which
        // would drop a guaranteed first weather report). Appearing/strengthening
        // still get new-snapshot backing.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainWeakeningReportJson);

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, prior: null));
    }

    [Fact]
    public async Task PriorAware_MalformedPriorBody_FallsBackToNewOnly_Succeeds()
    {
        // A prior whose body can't be parsed is non-fatal: the cycle logs a WARN and
        // the prior-aware check degenerates to new-only backing rather than failing.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingAlignedReportJson);

        Assert.IsType<ReconcileResult.Success>(
            await RunReconciler(responseJson, prior: PriorOf("{ this is not valid json")));
    }

    [Fact]
    public async Task AnchoredProse_DayPartWordOutsideChangeWindow_Degrades()
    {
        // Genuine onset (dry→rain), but the prose times it "this evening" while the
        // change window (11-17Z) is morning in CDT — a narrative/window
        // contradiction the {q:time} check can't see (no token).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingEveningProseReportJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(
            await RunReconciler(responseJson, tz: Cdt, prior: PriorOf(DryPriorSnapshotJson)));
        Assert.Contains("contradicts the change's own window", degraded.Reason);
    }

    [Fact]
    public async Task AnchoredProse_InWindowWordPlusTransition_NotRejected_Succeeds()
    {
        // "this morning, tapering by evening": morning is in-window, so the sentence
        // is on-window and the trailing transition word must not trigger a rejection.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RainAppearingTransitionProseReportJson);

        Assert.IsType<ReconcileResult.Success>(
            await RunReconciler(responseJson, tz: Cdt, prior: PriorOf(DryPriorSnapshotJson)));
    }

    // ── WX-152 closing-claim validation ──────────────────────────────────────
    // Snapshots: first block is 2026-06-09T17:00:00Z = Tue 12:00 CDT (afternoon), so
    // refDate = Tue 6/9; "tonight" = Tue 18:00 → Wed 06:00 local (the 23Z + 05Z blocks).

    // Tue afternoon thunderstorm, then dry Tue-evening and Wed-overnight.
    private const string ClosingStormAfternoonDrySnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-09T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":8,"max":16},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},
          {"startUtc":"2026-06-09T23:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":20,"max":26},"windKt":{"min":5,"max":10},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false},
          {"startUtc":"2026-06-10T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":18,"max":22},"windKt":{"min":3,"max":8},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false}
        ]}
        """;

    // Tue afternoon dry, but a Tue-evening (tonight) thunderstorm — backs a "tonight" claim.
    private const string ClosingStormEveningSnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-09T17:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":8,"max":16},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false},
          {"startUtc":"2026-06-09T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":20,"max":26},"windKt":{"min":5,"max":10},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},
          {"startUtc":"2026-06-10T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":18,"max":22},"windKt":{"min":3,"max":8},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false}
        ]}
        """;

    // Only Tue afternoon — nothing for "tomorrow" (beyond-horizon guard).
    private const string ClosingTodayOnlySnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-09T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":8,"max":16},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false}
        ]}
        """;

    private static string ClosingOnlyReport(string closing) =>
        "{\"schemaVersion\":5,\"changes\":[],\"narrative\":{\"en\":{\"changeSummary\":null,\"closing\":"
        + JsonSerializer.Serialize(closing) + "}}}";

    [Fact]
    public async Task ClosingClaim_StormTonight_DryEveningOvernight_Degrades()
    {
        // The send-1995 repro: closing asserts a storm "tonight" while the snapshot is
        // dry every block from this evening on (the lone storm is this afternoon).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Tonight carries a modest chance of a storm before conditions settle, with the rest of the week looking calm."));

        var degraded = Assert.IsType<ReconcileResult.Degraded>(
            await RunReconciler(responseJson, tz: Cdt));
        Assert.Contains("entirely dry", degraded.Reason);
    }

    [Fact]
    public async Task ClosingClaim_StaysDry_NotRejected_Succeeds()
    {
        // "stays mostly dry … no organized rain expected" — negated, must not be flagged.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The week ahead stays mostly dry, with no organized rain expected through the weekend."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_UntimedStorm_NotRejected_Succeeds()
    {
        // A storm named with no resolvable time can't be localized — residual, leans on
        // the prompt, must not be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Any storm that does develop this week could be lively, even if the odds of seeing one remain low."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_PrecipAtCorrectTime_NotRejected_Succeeds()
    {
        // "a storm this afternoon" — the afternoon block carries a thunderstorm, so the
        // claim is supported and must not be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A storm is likely this afternoon before things quiet down."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_StormTonight_EveningHasStorm_NotRejected_Succeeds()
    {
        // Same "storm tonight" prose, but here the snapshot's evening block backs it.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormEveningSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Tonight carries a modest chance of a storm before conditions settle, with the rest of the week looking calm."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_BeyondHorizon_NotRejected_Succeeds()
    {
        // "rain tomorrow" but the snapshot has no Wednesday block — can't verify, so
        // the check skips it (beyond-horizon guard) rather than false-rejecting.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingTodayOnlySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Rain is likely tomorrow across the area as a wet pattern arrives."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_CessationPhrasing_NotRejected_Succeeds()
    {
        // "Showers tapering off this evening" — the evening time word is a deadline by
        // which precip ENDS, not where it occurs; the dry evening block is consistent,
        // so it must NOT be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Showers are tapering off this evening, leaving a calm and quiet night."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_HedgedRainTonight_DryTonight_Degrades()
    {
        // "A little rain tonight" is still an assertion — a dry-tonight snapshot
        // contradicts it. (Guards that "little" was dropped from the skip cues.)
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A little rain is likely tonight before drier air arrives."));

        var degraded = Assert.IsType<ReconcileResult.Degraded>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Contains("entirely dry", degraded.Reason);
    }

    [Fact]
    public async Task ClosingClaim_WeekdayPinned_NotRejected_Succeeds()
    {
        // "Saturday afternoon" is pinned to a specific weekday we don't localize — it
        // must not be mis-resolved to TODAY's (dry) afternoon and rejected. (Snapshot's
        // Tue afternoon is dry, so a mis-resolution would fire.)
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormEveningSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A storm is likely Saturday afternoon as the next system arrives."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_MultipleTimeWords_Ambiguous_NotRejected_Succeeds()
    {
        // Two resolvable time words in one sentence is ambiguous (which one does the
        // storm attach to?) — skip rather than risk a false reject. (Afternoon is dry
        // here, so picking it would fire; the ambiguity guard prevents that.)
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormEveningSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A storm is possible this afternoon or this evening."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    // A fixed UTC-5 zone (US Central in June / CDT) used by the WX-149 prose-token
    // tests: a {q:time} token at 12:00Z renders to 7:00 AM local — "morning", not
    // "afternoon". A custom fixed-offset zone keeps the test deterministic across
    // platforms (no dependency on the host's IANA/Windows time-zone database).
    private static readonly TimeZoneInfo Cdt =
        TimeZoneInfo.CreateCustomTimeZone("Test-CDT", TimeSpan.FromHours(-5), "Test CDT", "Test CDT");

    private static async Task<ReconcileResult> RunReconciler(string anthropicResponseJson, bool allowSkip = false, TimeZoneInfo? tz = null, ForecastSnapshot? prior = null)
        => await RunReconciler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(anthropicResponseJson, Encoding.UTF8, "application/json"),
        }, allowSkip, tz: tz, prior: prior);

    private static async Task<ReconcileResult> RunReconciler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool allowSkip = false, string[]? narrativeLanguages = null, TimeZoneInfo? tz = null, ForecastSnapshot? prior = null)
    {
        var http = new HttpClient(new StubHandler(respond));
        var claude = new ClaudeClient(http, apiKey: "test-key", model: "claude-sonnet-4-6", personaPrefix: "Persona text.");
        var reconciler = new ForecastReconciler(claude);

        return await reconciler.ReconcileAsync(
            snapshot: BuildSnapshot(),
            provisional: new ForecastSnapshotBody(),
            gfsModelRunUtc: DateTime.UtcNow,
            tafIssuanceUtc: null,
            tafValidToUtc: null,
            prior: prior,
            narrativeLanguages: narrativeLanguages ?? new[] { "en" },
            tz: tz ?? Cdt,
            changeSeverity: ChangeSeverity.None,
            previousMetarIcao: null,
            allowSkip: allowSkip,
            changedSinceLastSend: Array.Empty<TriggerSource>());
    }

    // WX-151: wrap a final_snapshot JSON as a prior ForecastSnapshot for the
    // prior-aware change-verification tests.
    private static ForecastSnapshot PriorOf(string bodyJson) => new()
    {
        StationIcao = "KTEST",
        GeneratedAtUtc = new DateTime(2026, 6, 9, 18, 0, 0, DateTimeKind.Utc),
        SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
        Body = bodyJson,
    };

    private static WeatherSnapshot BuildSnapshot() => new()
    {
        ObservationAvailable = true,
        StationIcao = "KTEST",
        LocalityName = "Test Locality",
        ObservationTimeUtc = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc),
    };

    private static string BuildClaudeResponseJson(
        string finalSnapshotJson,
        string reasoningTrace,
        int inputTokens, int outputTokens,
        int cacheReadInputTokens, int cacheCreationInputTokens,
        string structuredReportJson = ValidStructuredReportJson)
    {
        var input = new
        {
            final_snapshot = JsonDocument.Parse(finalSnapshotJson).RootElement,
            structured_report = JsonDocument.Parse(structuredReportJson).RootElement,
            reasoning_trace = reasoningTrace,
        };
        var inputJson = JsonSerializer.Serialize(input);
        return BuildClaudeResponseJsonWithRawInput(inputJson, inputTokens, outputTokens, cacheReadInputTokens, cacheCreationInputTokens);
    }

    private static string BuildClaudeResponseJsonWithRawInput(
        string toolInputJson,
        int inputTokens = 10,
        int outputTokens = 10,
        int cacheReadInputTokens = 0,
        int cacheCreationInputTokens = 0,
        string toolName = "submit_reconciled_report",
        string stopReason = "tool_use")
    {
        return $$"""
            {
              "id": "msg_test",
              "type": "message",
              "role": "assistant",
              "content": [
                {
                  "type": "tool_use",
                  "id": "toolu_test",
                  "name": "{{toolName}}",
                  "input": {{toolInputJson}}
                }
              ],
              "model": "claude-sonnet-4-6",
              "stop_reason": "{{stopReason}}",
              "usage": {
                "input_tokens": {{inputTokens}},
                "output_tokens": {{outputTokens}},
                "cache_read_input_tokens": {{cacheReadInputTokens}},
                "cache_creation_input_tokens": {{cacheCreationInputTokens}}
              }
            }
            """;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}