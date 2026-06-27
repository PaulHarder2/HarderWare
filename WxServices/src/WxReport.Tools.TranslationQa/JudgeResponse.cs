namespace WxReport.Tools.TranslationQa;

/// <summary>The independent model's self-reported fluency / how far to trust its review.</summary>
public sealed record JudgeConfidence(string Level, string Note);

/// <summary>A faithful back-translation of one target-language report to English.</summary>
public sealed record BackTranslation(string Scenario, string English);

/// <summary>A report-level problem the judge flagged, with where it is and how to fix it.</summary>
public sealed record ReportFinding(string Scenario, string Location, string Problem, string SuggestedFix);

/// <summary>The judge's verdict on one controlled-vocabulary term.</summary>
public sealed record VocabularyVerdict(string Token, bool Accurate, bool Natural, string Comment, string? Suggestion);

/// <summary>
/// WX-218 — the parsed, validated verdict an independent (non-Claude) model returns for a judging
/// request. Mirrors the response shape WX-217's preamble dictates. Produced from a manual paste today
/// (<see cref="ManualPasteJudge"/>) and, in future, an automated non-Claude API behind the same
/// <see cref="IJudge"/> seam.
/// </summary>
public sealed record JudgeResponse(
    string Language,
    JudgeConfidence? SelfReportedConfidence,
    IReadOnlyList<BackTranslation> BackTranslations,
    IReadOnlyList<ReportFinding> ReportFindings,
    IReadOnlyList<VocabularyVerdict> VocabularyVerdicts);