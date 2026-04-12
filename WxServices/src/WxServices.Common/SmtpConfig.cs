namespace WxServices.Common;

/// <summary>
/// SMTP connection and credential settings shared across all services.
/// Non-secret settings (Host, Port) are bound from the <c>Smtp</c> section of
/// appsettings files.  Credentials (Username, Password, FromAddress) are stored
/// in the <c>GlobalSettings</c> database row and loaded at runtime.
/// </summary>
public class SmtpConfig
{
    public string  Host        { get; set; } = "smtp.gmail.com";
    public int     Port        { get; set; } = 587;
    public string? Username    { get; set; }

    /// <summary>App password or SMTP password — stored in the GlobalSettings database row.</summary>
    public string? Password    { get; set; }
    public string? FromAddress { get; set; }
}
