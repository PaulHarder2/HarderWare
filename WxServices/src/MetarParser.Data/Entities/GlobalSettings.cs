namespace MetarParser.Data.Entities;

/// <summary>
/// Single-row table (Id is always 1) that stores application-wide secrets.
/// Supersedes the <c>Smtp:Username</c>, <c>Smtp:Password</c>, <c>Smtp:FromAddress</c>,
/// and <c>Claude:ApiKey</c> entries that previously had to be placed in
/// <c>appsettings.local.json</c>.
/// </summary>
/// <remarks>
/// Non-secret SMTP settings (<c>Host</c>, <c>Port</c>) remain in
/// <c>appsettings.shared.json</c> because they are not sensitive and do not vary by user.
/// <para>
/// All services read secrets from this row first; if a field is <see langword="null"/>
/// they fall back to the corresponding <c>appsettings.local.json</c> value so that
/// existing installations continue to work until the database row is populated.
/// </para>
/// </remarks>
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
