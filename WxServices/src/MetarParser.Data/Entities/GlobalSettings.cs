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
    public int     Id              { get; set; }

    /// <summary>Claude API key used by WxReport.Svc for weather report generation.</summary>
    public string? ClaudeApiKey    { get; set; }

    /// <summary>SMTP account username (typically a Gmail address).</summary>
    public string? SmtpUsername    { get; set; }

    /// <summary>SMTP app password.</summary>
    public string? SmtpPassword    { get; set; }

    /// <summary>From address for outgoing emails.</summary>
    public string? SmtpFromAddress { get; set; }
}
