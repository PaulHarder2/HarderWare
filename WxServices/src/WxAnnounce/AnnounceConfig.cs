namespace WxAnnounce;

/// <summary>Configuration for the WxAnnounce console application, bound from the "Announce" section.</summary>
public class AnnounceConfig
{
    /// <summary>Path to the plain-text file containing the announcement to send.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>
    /// Maximum age of the announcement file in minutes.
    /// Sends are aborted if the file's last-write timestamp is older than this value,
    /// guarding against accidental re-runs of a stale command.
    /// </summary>
    public int MaxAgeMinutes { get; set; } = 10;
}

/// <summary>
/// Minimal recipient fields needed by WxAnnounce.
/// Bound from the same <c>Report:Recipients</c> array used by WxReport.Svc
/// so the subscriber list does not need to be maintained in two places.
/// </summary>
public class AnnounceRecipient
{
    /// <summary>Recipient email address.</summary>
    public string Email { get; set; } = "";

    /// <summary>Recipient display name, used in the To: header.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Natural language name for the announcement (e.g. <c>"English"</c>, <c>"Spanish"</c>).
    /// Falls back to <c>Report:DefaultLanguage</c> when <see langword="null"/>.
    /// </summary>
    public string? Language { get; set; }
}
