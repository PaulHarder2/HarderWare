namespace WxReport.Svc.TranslationQa;

/// <summary>
/// WX-227 — configuration for the Gemini API judge. Since WX-235 the <see cref="ApiKey"/> is read
/// from the database (<c>GlobalSettings.GeminiApiKey</c>, set via WxManager → Configure), never from
/// a config file, and it is used both by the <c>WxReport.Tools.TranslationQa</c> dev tool and by the
/// in-service <c>QaRerunWorker</c>. An optional <c>appsettings.local.json</c> "Gemini" section at the
/// InstallRoot supplies only non-secret overrides (model, request timeout); any <c>ApiKey</c> there is
/// ignored (always overwritten by the DB value).
/// </summary>
public sealed class GeminiConfig
{
    /// <summary>Gemini API key (from Google AI Studio). Read from the DB (<c>GlobalSettings.GeminiApiKey</c>) since WX-235; never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model id. A free-tier "flash" model handles the reasoning + JSON output this judge needs; override to whatever is current.</summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>Per-request HTTP timeout, seconds. A full-vocabulary audit is one sizeable call.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>API base URL (the Generative Language endpoint).</summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
}