namespace WxServices.Common.TranslationQa;

/// <summary>One scenario's rendered report pair — the English reference and the target rendering.</summary>
public sealed record RenderedScenario(string Name, string Synopsis, string EnglishHtml, string TargetHtml);

/// <summary>
/// One controlled-vocabulary token, its English source paired with the target-language rendering and the
/// generation metadata the judge needs to assess the term in context.
/// </summary>
public sealed record VocabularyPair(
    string Token,
    string EnglishPhrase,
    string EnglishContext,
    string ContextKind,
    string TargetPhrase,
    string TargetContext,
    bool Representable,
    string? Note,
    bool Reviewed);

/// <summary>
/// WX-218 — the structured judging request, persisted as <c>&lt;iso&gt;.&lt;stamp&gt;.request.json</c> at
/// generate time so the judge and artifact steps work from <b>exactly</b> what the judge saw, without a
/// second (non-deterministic) Claude reconciliation. The Markdown request (WX-217) is the human-/LLM-
/// facing form of the same content; this is the machine-readable form WX-219's review tab joins with the
/// verdict.
///
/// Lives in WxServices.Common (WX-219) as part of the shared judge-package contract.
/// </summary>
public sealed record JudgingRequest(
    string TargetIso,
    string? TargetDisplayName,
    IReadOnlyList<RenderedScenario> Scenarios,
    IReadOnlyList<VocabularyPair> Vocabulary);