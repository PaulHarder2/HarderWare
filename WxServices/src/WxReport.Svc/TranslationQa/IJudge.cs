using WxServices.Common.TranslationQa;

namespace WxReport.Svc.TranslationQa;

/// <summary>
/// WX-218 — the pluggable judge seam (a Strategy). It abstracts only the part that differs between an
/// independent reviewer reached by hand and one reached by API: <b>where the raw reply comes from</b>.
/// The parsing/validation is shared (<see cref="JudgeResponseParser"/>), so an automated non-Claude
/// judge (e.g. <see cref="GeminiJudge"/>) is a small implementation with no change to callers.
/// </summary>
public interface IJudge
{
    /// <summary>
    /// Return the parsed verdict for the given request. The request markdown is what an API judge would
    /// send; the manual implementation ignores it (the operator already pasted it into the model by hand).
    /// </summary>
    Task<JudgeResponse> JudgeAsync(string requestMarkdown, CancellationToken ct);

    /// <summary>A short provenance label stamped into <see cref="JudgeResponse.JudgedBy"/> — e.g. <c>"gemini (model)"</c> or <c>"manual-paste"</c>.</summary>
    string SourceLabel { get; }
}

/// <summary>Thrown when a reply cannot be parsed into a <see cref="JudgeResponse"/>; carries an operator-facing reason.</summary>
public sealed class JudgeParseException : Exception
{
    public JudgeParseException(string message) : base(message) { }
    public JudgeParseException(string message, Exception inner) : base(message, inner) { }
}