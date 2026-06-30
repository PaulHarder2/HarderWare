using WxReport.Svc.TranslationQa;

namespace WxReport.Tools.TranslationQa;

/// <summary>
/// The manual-paste MVP judge (Phase 2): the operator pasted the request into Copilot/ChatGPT and saved the
/// reply to a file; this reads that file and parses it. The <paramref name="requestMarkdown"/> argument is
/// unused here by design (the request was delivered out of band) — it exists for the API implementation
/// (<see cref="GeminiJudge"/>). The seam (<see cref="IJudge"/>) and parser now live in WxReport.Svc so the
/// service can regenerate packages too (WX-235); this tool keeps the manual fallback.
/// </summary>
public sealed class ManualPasteJudge(string replyFilePath) : IJudge
{
    public string SourceLabel => "manual-paste";

    public async Task<JudgeResponse> JudgeAsync(string requestMarkdown, CancellationToken ct)
    {
        var raw = await File.ReadAllTextAsync(replyFilePath, ct);
        if (!JudgeResponseParser.TryParse(raw, out var response, out var error))
            throw new JudgeParseException(error!);
        return response!;
    }
}