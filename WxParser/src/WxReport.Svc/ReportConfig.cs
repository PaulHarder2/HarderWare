// Configuration model for the WxReport.Svc service.
// Populated from the "Report" section of appsettings files.
// Secrets (ApiKey, Smtp.Password) must be provided in appsettings.local.json,
// which is excluded from source control.

namespace WxReport.Svc;

public class ReportConfig
{
    /// <summary>Language to use when no per-recipient language is specified (e.g. "English", "Spanish").</summary>
    public string DefaultLanguage         { get; set; } = "English";

    /// <summary>Hour of day (0–23, in the recipient's local timezone) at which the daily scheduled report is sent.</summary>
    public int    DefaultScheduledSendHour { get; set; } = 7;

    /// <summary>Minimum minutes that must elapse between any two reports sent to the same recipient.</summary>
    public int    MinGapMinutes           { get; set; } = 60;

    /// <summary>Check interval; loaded from appsettings.json (service-specific).</summary>
    public int    IntervalMinutes         { get; set; } = 5;

    public SignificantChangeConfig SignificantChange { get; set; } = new();
    public ClaudeConfig            Claude            { get; set; } = new();
    public SmtpConfig              Smtp              { get; set; } = new();
    public List<RecipientConfig>   Recipients        { get; set; } = [];
}

public class SignificantChangeConfig
{
    /// <summary>Wind speed at or above this threshold (kt) is considered a significant condition.</summary>
    public int    WindThresholdKt       { get; set; } = 25;

    /// <summary>Visibility below this threshold (SM) is considered a significant condition.</summary>
    public double VisibilityThresholdSm { get; set; } = 3.0;

    /// <summary>Ceiling below this threshold (ft AGL) is considered a significant condition.</summary>
    public int    CeilingThresholdFt    { get; set; } = 3000;
}

public class ClaudeConfig
{
    public string? ApiKey { get; set; }

    /// <summary>Claude model ID to use for text generation.</summary>
    public string  Model  { get; set; } = "claude-haiku-4-5-20251001";
}

public class SmtpConfig
{
    public string  Host        { get; set; } = "smtp.gmail.com";
    public int     Port        { get; set; } = 587;
    public string? Username    { get; set; }

    /// <summary>App password or SMTP password — must come from appsettings.local.json.</summary>
    public string? Password    { get; set; }
    public string? FromAddress { get; set; }
    public string  FromName    { get; set; } = "WxReport";
}

public class RecipientConfig
{
    public string  Email             { get; set; } = "";
    public string  Name              { get; set; } = "";

    /// <summary>Language for this recipient's reports (e.g. "English", "Spanish", "French").  Null falls back to <see cref="ReportConfig.DefaultLanguage"/>.</summary>
    public string? Language          { get; set; }

    /// <summary>IANA timezone name (e.g. "America/Chicago").  Defaults to UTC.</summary>
    public string  Timezone          { get; set; } = "UTC";

    /// <summary>Hour of day (0–23) in the recipient's timezone at which the daily report is sent.  Null falls back to <see cref="ReportConfig.DefaultScheduledSendHour"/>.</summary>
    public int?    ScheduledSendHour { get; set; }
}
