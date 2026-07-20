namespace MetarParser.Data.Entities;

/// <summary>
/// Single-row table (Id is always 1) that stores application-wide secrets.
/// Services read credentials exclusively from this row — secrets never appear
/// in config files.  Non-secret SMTP settings (<c>Host</c>, <c>Port</c>) remain
/// in <c>appsettings.shared.json</c>.  Use WxManager → Configure to edit.
/// </summary>
public class GlobalSettings
{
    /// <summary>Primary key — always 1.  Exactly one row is valid in this table.</summary>
    public int Id { get; set; }

    /// <summary>Claude API key used by WxReport.Svc for weather report generation.</summary>
    public string? ClaudeApiKey { get; set; }

    /// <summary>Gemini API key used by WxReport.Svc's translation-QA judge when regenerating a judge package (WX-235).</summary>
    public string? GeminiApiKey { get; set; }

    /// <summary>SMTP account username (typically a Gmail address).</summary>
    public string? SmtpUsername { get; set; }

    /// <summary>SMTP app password.</summary>
    public string? SmtpPassword { get; set; }

    /// <summary>From address for outgoing emails.</summary>
    public string? SmtpFromAddress { get; set; }

    /// <summary>
    /// What3Words API key, used by <c>AddressGeocoder</c> to resolve
    /// <c>///word.word.word</c> addresses in WxManager's Recipients tab (WX-322).
    /// <para>
    /// Moved here from <c>appsettings.local.json</c> because a file is a
    /// demonstrably unsafe home for it: the Configure tab's pre-WX-315 save path
    /// rebuilt that file from a fixed key list and silently deleted this key,
    /// leaving What3Words lookups broken for roughly six weeks before anyone
    /// connected the two.  Secrets belong in this row — the sole exception being
    /// the connection string, which cannot live in the store it unlocks.
    /// </para>
    /// </summary>
    public string? What3WordsApiKey { get; set; }
}