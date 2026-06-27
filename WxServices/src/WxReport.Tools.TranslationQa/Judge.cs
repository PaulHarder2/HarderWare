namespace WxReport.Tools.TranslationQa;

/// <summary>
/// WX-218 — the pluggable judge seam (a Strategy). It abstracts only the part that differs between an
/// independent reviewer reached by hand and one reached by API: <b>where the raw reply comes from</b>.
/// The parsing/validation is shared (<see cref="JudgeResponseParser"/>), so a future automated
/// non-Claude judge (OpenAI/Gemini) is a small new implementation with no change to callers.
/// </summary>
public interface IJudge
{
    /// <summary>
    /// Return the parsed verdict for the given request. The request markdown is what an API judge would
    /// send; the manual implementation ignores it (the operator already pasted it into the model by hand).
    /// </summary>
    Task<JudgeResponse> JudgeAsync(string requestMarkdown, CancellationToken ct);
}

/// <summary>
/// The manual-paste MVP judge: the operator pasted the request into Copilot/ChatGPT and saved the reply
/// to a file; this reads that file and parses it. The <paramref name="requestMarkdown"/> argument is
/// unused here by design (the request was delivered out of band) — it exists for the API implementation.
/// </summary>
public sealed class ManualPasteJudge(string replyFilePath) : IJudge
{
    public async Task<JudgeResponse> JudgeAsync(string requestMarkdown, CancellationToken ct)
    {
        var raw = await File.ReadAllTextAsync(replyFilePath, ct);
        if (!JudgeResponseParser.TryParse(raw, out var response, out var error))
            throw new JudgeParseException(error!);
        return response!;
    }
}

/// <summary>Thrown when a reply cannot be parsed into a <see cref="JudgeResponse"/>; carries an operator-facing reason.</summary>
public sealed class JudgeParseException : Exception
{
    public JudgeParseException(string message) : base(message) { }
    public JudgeParseException(string message, Exception inner) : base(message, inner) { }
}