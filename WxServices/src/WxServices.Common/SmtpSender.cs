using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using WxServices.Logging;

namespace WxServices.Common;

/// <summary>
/// Sends plain-text or HTML emails via SMTP using MailKit.
/// Configured for STARTTLS on port 587 (compatible with Gmail App Password authentication).
/// The From display name is fixed at construction time; credentials are supplied via
/// <see cref="SmtpConfig"/>.  SMTP secrets are stored in the <c>GlobalSettings</c> database
/// row and loaded by each service at runtime.
/// Supports inline image attachments referenced by <c>cid:</c> URIs in the HTML body.
/// </summary>
public sealed class SmtpSender
{
    private readonly SmtpConfig _cfg;
    private readonly string     _fromName;

    /// <summary>
    /// Initializes a new instance of <see cref="SmtpSender"/>.
    /// </summary>
    /// <param name="cfg">SMTP connection and credential settings bound from the top-level <c>Smtp</c> config section.</param>
    /// <param name="fromName">
    /// Display name used in the <c>From:</c> header (e.g. <c>"WxReport"</c> or <c>"WxMonitor"</c>).
    /// This is intentionally not configurable — each service hardcodes its own identity.
    /// </param>
    public SmtpSender(SmtpConfig cfg, string fromName)
    {
        _cfg      = cfg;
        _fromName = fromName;
    }

    /// <summary>
    /// Sends an email.  When <paramref name="htmlBody"/> is provided the message is sent
    /// as <c>multipart/alternative</c> (plain-text first, HTML second) so that clients
    /// without HTML support still receive a readable version.  When <paramref name="htmlBody"/>
    /// is <see langword="null"/> the message is sent as plain text only.
    /// </summary>
    /// <remarks>
    /// When <paramref name="inlineImages"/> is non-empty the HTML part is wrapped in a
    /// <c>multipart/related</c> container so that each image can be referenced by
    /// <c>&lt;img src="cid:content-id"&gt;</c> in the HTML.  The keys in
    /// <paramref name="inlineImages"/> must match the <c>cid:</c> values used in the HTML,
    /// without the <c>cid:</c> prefix (e.g. key <c>"meteogram24h"</c> for
    /// <c>src="cid:meteogram24h"</c>).
    /// </remarks>
    /// <param name="toAddress">Recipient email address.</param>
    /// <param name="subject">Email subject line.</param>
    /// <param name="plainBody">Plain-text email body, used as the primary body or as the fallback part in a multipart message.</param>
    /// <param name="htmlBody">
    /// Optional HTML email body.  When supplied, the message is sent as
    /// <c>multipart/alternative</c> with <paramref name="plainBody"/> as the plain-text part.
    /// </param>
    /// <param name="inlineImages">
    /// Optional dictionary mapping content-id strings to local file paths for inline images.
    /// Ignored when <paramref name="htmlBody"/> is <see langword="null"/>.
    /// </param>
    /// <param name="toName">
    /// Recipient display name used in the <c>To:</c> header.
    /// Defaults to <paramref name="toAddress"/> when <see langword="null"/>.
    /// </param>
    /// <param name="ct">Cancellation token propagated to all SMTP operations so that host shutdown aborts an in-flight send.</param>
    /// <returns><see langword="true"/> if the message was accepted by the SMTP server; <see langword="false"/> on any failure.</returns>
    /// <sideeffects>Opens an SMTP connection to the configured host and sends an email. Writes error log entries on failure.</sideeffects>
    public async Task<bool> SendAsync(
        string toAddress, string subject, string plainBody,
        string? htmlBody = null,
        IReadOnlyDictionary<string, string>? inlineImages = null,
        string? toName = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            Logger.Error($"{_fromName}: SendAsync called with null or empty toAddress — cannot send.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_cfg.Username)   ||
            string.IsNullOrWhiteSpace(_cfg.Password)   ||
            string.IsNullOrWhiteSpace(_cfg.FromAddress))
        {
            Logger.Error("SMTP credentials are not configured. Use WxManager → Configure to set SMTP credentials.");
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_fromName, _cfg.FromAddress));
            message.To.Add(new MailboxAddress(toName ?? toAddress, toAddress));
            message.Subject = subject;

            if (htmlBody is not null)
            {
                var htmlPart = new TextPart("html") { Text = htmlBody };

                MimeEntity richPart;
                if (inlineImages is { Count: > 0 })
                {
                    // Wrap the HTML and its inline images in multipart/related.
                    var related = new MultipartRelated { htmlPart };
                    foreach (var (cid, filePath) in inlineImages)
                    {
                        if (!File.Exists(filePath))
                        {
                            Logger.Warn($"{_fromName}: inline image not found, skipping: {filePath}");
                            continue;
                        }
                        var img = new MimePart("image", "png")
                        {
                            Content            = new MimeContent(File.OpenRead(filePath)),
                            ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                            ContentId          = cid,
                        };
                        related.Add(img);
                    }
                    richPart = related;
                }
                else
                {
                    richPart = htmlPart;
                }

                message.Body = new MultipartAlternative
                {
                    new TextPart("plain") { Text = plainBody },
                    richPart,
                };
            }
            else
            {
                message.Body = new TextPart("plain") { Text = plainBody };
            }

            using var client = new SmtpClient();
            await client.ConnectAsync(_cfg.Host, _cfg.Port, SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(_cfg.Username, _cfg.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to send email to {toAddress}: {ex.Message}");
            return false;
        }
    }
}
