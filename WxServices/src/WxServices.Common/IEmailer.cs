namespace WxServices.Common;

/// <summary>
/// Abstraction over sending an email, extracted so callers (and tests) can depend on the
/// capability rather than the concrete <see cref="SmtpSender"/>.  <see cref="SmtpSender"/> is
/// the production implementation; tests substitute a capturing fake.  The signature is identical
/// to <see cref="SmtpSender.SendAsync"/> so the existing class satisfies it without change.
/// </summary>
public interface IEmailer
{
    /// <summary>Sends an email.  Returns <see langword="true"/> if the SMTP server accepted the message.</summary>
    Task<bool> SendAsync(
        string toAddress, string subject, string plainBody,
        string? htmlBody = null,
        IReadOnlyDictionary<string, string>? inlineImages = null,
        string? toName = null,
        CancellationToken ct = default);
}