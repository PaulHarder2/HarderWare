using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using WxParser.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Sends alert emails via SMTP using MailKit.
/// </summary>
public sealed class AlertEmailSender
{
    private readonly MonitorSmtpConfig _cfg;

    /// <summary>Initializes a new instance of <see cref="AlertEmailSender"/> with the given SMTP configuration.</summary>
    /// <param name="cfg">SMTP connection and credential settings for the monitor alert account.</param>
    public AlertEmailSender(MonitorSmtpConfig cfg) => _cfg = cfg;

    /// <summary>
    /// Sends a plain-text alert to the configured alert address.
    /// Returns true on success, false on failure.
    /// </summary>
    /// <param name="toAddress">Recipient email address for the alert.</param>
    /// <param name="subject">Alert email subject line.</param>
    /// <param name="body">Plain-text alert body.</param>
    /// <returns><see langword="true"/> if the message was accepted by the SMTP server; <see langword="false"/> on any failure.</returns>
    /// <sideeffects>Opens an SMTP connection to the configured host and sends an email. Writes an error log entry on failure.</sideeffects>
    public async Task<bool> SendAsync(string toAddress, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            Logger.Error("AlertEmailSender.SendAsync called with null or empty toAddress — cannot send.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_cfg.Username)   ||
            string.IsNullOrWhiteSpace(_cfg.Password)   ||
            string.IsNullOrWhiteSpace(_cfg.FromAddress))
        {
            Logger.Error("SMTP credentials are not configured. Set Monitor.Smtp.Username, Password, and FromAddress in appsettings.local.json.");
            return false;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_cfg.FromName, _cfg.FromAddress));
        message.To.Add(new MailboxAddress(toAddress, toAddress));
        message.Subject = subject;
        message.Body    = new TextPart("plain") { Text = body };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_cfg.Host, _cfg.Port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_cfg.Username, _cfg.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(quit: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to send alert email to {toAddress}: {ex.Message}");
            return false;
        }
    }
}
