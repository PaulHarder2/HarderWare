using Microsoft.Extensions.Configuration;

namespace MetarParser.Data;

/// <summary>
/// Controls how <see cref="DatabaseSetup.EnsureSchemaAsync"/> retries a
/// SQL Server that is not yet reachable at service startup (WX-28).
/// <para>
/// The four WxServices boot with Windows, and after a Windows-Update-driven
/// reboot they race SQL Server's own service start.  Before WX-28 the first
/// <c>SqlException error 26</c> ("server not found") was terminal: the
/// service crashed, Windows SCM did not restart it on a reboot path, and
/// the operator woke up to four dead services and a flood of alert email.
/// With retry, transient connection failures are logged at <c>WARN</c>; the
/// service waits <see cref="DelaySecondsSchedule"/> seconds between attempts
/// and escalates to <c>ERROR</c> only after <see cref="MaxAttempts"/> have
/// all failed.
/// </para>
/// <para>
/// Defaults are 12 attempts with delays 5 s, 10 s, 20 s, 30 s, 30 s, …
/// (~5 minutes total) — sized to cover the worst-case post-reboot SQL
/// Server warm-up while a Windows Update installation competes for I/O.
/// Tunable via the <c>Database:StartupRetry</c> section of
/// <c>appsettings.shared.json</c> or <c>appsettings.local.json</c>, so a
/// rebuild is not required to adjust the schedule on a particular host.
/// </para>
/// </summary>
public sealed class DatabaseStartupRetryOptions
{
    public const int DefaultMaxAttempts = 12;

    public static readonly int[] DefaultDelaySecondsSchedule =
        { 5, 10, 20, 30, 30, 30, 30, 30, 30, 30, 30 };

    /// <summary>
    /// Total number of attempts (first try plus retries). Must be at least 1.
    /// </summary>
    public int MaxAttempts { get; init; } = DefaultMaxAttempts;

    /// <summary>
    /// Delay (seconds) to wait after each failing attempt before the next
    /// attempt.  Element 0 is the delay after attempt 1, element 1 after
    /// attempt 2, and so on; if the schedule is shorter than
    /// <see cref="MaxAttempts"/>–1 the final element is reused for the
    /// remaining retries.
    /// </summary>
    public int[] DelaySecondsSchedule { get; init; } = DefaultDelaySecondsSchedule;

    public static DatabaseStartupRetryOptions Default { get; } = new();

    /// <summary>
    /// Delay to wait after <paramref name="attempt"/> (1-based) before the
    /// next attempt.  Returns <see cref="TimeSpan.Zero"/> if the attempt is
    /// at or past <see cref="MaxAttempts"/> — the caller is about to throw
    /// and no further wait is required.
    /// </summary>
    public TimeSpan DelayAfterAttempt(int attempt)
    {
        if (attempt >= MaxAttempts) return TimeSpan.Zero;

        var schedule = DelaySecondsSchedule is { Length: > 0 }
            ? DelaySecondsSchedule
            : DefaultDelaySecondsSchedule;

        var idx = Math.Min(Math.Max(0, attempt - 1), schedule.Length - 1);
        var seconds = Math.Max(0, schedule[idx]);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Read a <see cref="DatabaseStartupRetryOptions"/> from the
    /// <c>Database:StartupRetry</c> configuration section.  Any missing or
    /// invalid value falls back to the in-code default.
    /// </summary>
    public static DatabaseStartupRetryOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("Database:StartupRetry");

        var maxAttempts = section.GetValue<int?>("MaxAttempts");
        var schedule = section.GetSection("DelaySecondsSchedule").Get<int[]>();

        return new DatabaseStartupRetryOptions
        {
            MaxAttempts = maxAttempts is > 0 ? maxAttempts.Value : DefaultMaxAttempts,
            DelaySecondsSchedule = schedule is { Length: > 0 } ? schedule : DefaultDelaySecondsSchedule,
        };
    }
}

/// <summary>
/// Thrown by <see cref="DatabaseSetup.EnsureSchemaAsync"/> when every retry
/// attempt has failed with a transient SQL Server connection error.  The
/// outer <c>try/catch</c> in each service's <c>Program.cs</c> logs this at
/// <c>ERROR</c> and the service exits, leaving Windows SCM recovery actions
/// to restart it.
/// </summary>
public sealed class DatabaseUnavailableException : Exception
{
    public DatabaseUnavailableException(string message, Exception? inner)
        : base(message, inner) { }
}
