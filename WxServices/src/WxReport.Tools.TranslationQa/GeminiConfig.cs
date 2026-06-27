namespace WxReport.Tools.TranslationQa;

/// <summary>
/// WX-227 — configuration for the Gemini API judge. The <see cref="ApiKey"/> (and optionally a model
/// override) live in the tool-local <c>appsettings.local.json</c> "Gemini" section at the InstallRoot —
/// gitignored, never committed, and not in the shared config or the DB (the key is used only by this
/// dev tool, never by a service).
/// </summary>
public sealed class GeminiConfig
{
    /// <summary>Gemini API key (from Google AI Studio). Tool-local secret; never logged.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model id. A free-tier "flash" model handles the reasoning + JSON output this judge needs; override to whatever is current.</summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>Per-request HTTP timeout, seconds. A full-vocabulary audit is one sizeable call.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>API base URL (the Generative Language endpoint).</summary>
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com";
}