using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using WxParser.Logging;

namespace WxReport.Svc;

/// <summary>
/// Sends weather report emails via SMTP using MailKit.
/// Configured for Gmail with App Password authentication (STARTTLS on port 587).
/// </summary>
public sealed class EmailSender
{
    private readonly SmtpConfig _cfg;

    /// <summary>Initializes a new instance of <see cref="EmailSender"/> with the given SMTP configuration.</summary>
    /// <param name="cfg">SMTP connection and credential settings.</param>
    public EmailSender(SmtpConfig cfg) => _cfg = cfg;

    /// <summary>
    /// Sends a plain-text weather report to <paramref name="toAddress"/>.
    /// Returns true on success, false if the send fails.
    /// </summary>
    /// <param name="toAddress">Recipient email address.</param>
    /// <param name="toName">Recipient display name used in the <c>To:</c> header.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="body">Plain-text email body.</param>
    /// <returns><see langword="true"/> if the message was accepted by the SMTP server; <see langword="false"/> on any failure.</returns>
    /// <sideeffects>Opens an SMTP connection to the configured host and sends an email. Writes an error log entry on failure.</sideeffects>
    public async Task<bool> SendAsync(
        string toAddress, string toName, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            Logger.Error("EmailSender.SendAsync called with null or empty toAddress — cannot send.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_cfg.Username)   ||
            string.IsNullOrWhiteSpace(_cfg.Password)   ||
            string.IsNullOrWhiteSpace(_cfg.FromAddress))
        {
            Logger.Error("SMTP credentials are not configured. Set Report.Smtp.Username, Password, and FromAddress in appsettings.local.json.");
            return false;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_cfg.FromName, _cfg.FromAddress));
        message.To.Add(new MailboxAddress(toName, toAddress));
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
            Logger.Error($"Failed to send email to {toAddress}: {ex.Message}");
            return false;
        }
    }
}
