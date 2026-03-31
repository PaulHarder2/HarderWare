namespace WxServices.Common;

/// <summary>
/// SMTP connection and credential settings shared across all services.
/// Bound from the top-level <c>Smtp</c> section of appsettings files.
/// Secrets must come from <c>appsettings.local.json</c>.
/// The From display name is not configurable; each service supplies its own
/// when constructing a <see cref="SmtpSender"/>.
/// </summary>
public class SmtpConfig
{
    public string  Host        { get; set; } = "smtp.gmail.com";
    public int     Port        { get; set; } = 587;
    public string? Username    { get; set; }

    /// <summary>App password or SMTP password — must come from appsettings.local.json.</summary>
    public string? Password    { get; set; }
    public string? FromAddress { get; set; }
}
