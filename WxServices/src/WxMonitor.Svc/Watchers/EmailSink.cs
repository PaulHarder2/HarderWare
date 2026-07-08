using WxServices.Common;
using WxServices.Logging;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Delivers findings by email, rate-limited per finding via its <see cref="CooldownSlot"/>.
/// A finding whose category delivered within the cooldown window is skipped; the cooldown is
/// marked only on a <b>successful</b> send, so a failed SMTP attempt is retried next cycle rather
/// than burning the cooldown window. Constructed per cycle with the resolved emailer, destination,
/// and stamped "now".
/// </summary>
public sealed class EmailSink(IEmailer emailer, string alertEmail, TimeSpan cooldown, DateTime nowUtc, Action onSent) : ISink
{
    /// <inheritdoc/>
    public async Task EmitAsync(Finding finding, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(alertEmail))
        {
            Logger.Debug($"No alert email configured — not sending: {finding.Subject}");
            return;
        }

        if (finding.Cooldown is { LastSentUtc: { } last } && (nowUtc - last) < cooldown)
        {
            Logger.Debug($"Alert on cooldown, suppressed: {finding.Subject}");
            return;
        }

        Logger.Info($"Sending alert: {finding.Subject}");
        if (await emailer.SendAsync(alertEmail, finding.Subject, finding.Body, ct: ct))
        {
            finding.Cooldown?.MarkSent(nowUtc);
            onSent();
        }
    }
}