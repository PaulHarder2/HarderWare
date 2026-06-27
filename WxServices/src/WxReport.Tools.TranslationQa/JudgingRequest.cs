namespace WxReport.Tools.TranslationQa;

/// <summary>
/// WX-218 — the structured judging request, persisted as <c>&lt;iso&gt;.&lt;stamp&gt;.request.json</c> at
/// generate time so the judge and artifact steps work from <b>exactly</b> what the judge saw, without a
/// second (non-deterministic) Claude reconciliation. The Markdown request (WX-217) is the human-/LLM-
/// facing form of the same content; this is the machine-readable form WX-219 will join with the verdict.
/// </summary>
public sealed record JudgingRequest(
    string TargetIso,
    string? TargetDisplayName,
    IReadOnlyList<RenderedScenario> Scenarios,
    IReadOnlyList<VocabularyPair> Vocabulary);