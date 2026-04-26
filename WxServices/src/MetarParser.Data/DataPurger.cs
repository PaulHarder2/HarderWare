using Microsoft.EntityFrameworkCore;

using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Deletes stale weather records from the database to prevent unbounded growth.
/// </summary>
public static class DataPurger
{
    /// <summary>
    /// Deletes METAR records (and their child rows via cascade) whose
    /// <c>ObservationUtc</c> is older than <paramref name="retentionDays"/> days.
    /// </summary>
    /// <param name="dbOptions">EF Core options used to open a short-lived context.</param>
    /// <param name="retentionDays">Number of days of METAR history to retain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <sideeffects>Deletes rows from Metars (and child tables via cascade). Writes a log entry if any rows are removed.</sideeffects>
    public static async Task PurgeOldMetarsAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        int retentionDays,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
        await using var db = new WeatherDataContext(dbOptions);
        var deleted = await db.Metars
            .Where(m => m.ObservationUtc < cutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            Logger.Info($"DataPurger: deleted {deleted} METAR record(s) older than {retentionDays} days.");
    }

    /// <summary>
    /// Deletes TAF records (and their child rows via cascade) whose
    /// <c>IssuanceUtc</c> is older than <paramref name="retentionDays"/> days.
    /// </summary>
    /// <param name="dbOptions">EF Core options used to open a short-lived context.</param>
    /// <param name="retentionDays">Number of days of TAF history to retain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <sideeffects>Deletes rows from Tafs (and child tables via cascade). Writes a log entry if any rows are removed.</sideeffects>
    public static async Task PurgeOldTafsAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        int retentionDays,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
        await using var db = new WeatherDataContext(dbOptions);
        var deleted = await db.Tafs
            .Where(t => t.IssuanceUtc < cutoff)
            .ExecuteDeleteAsync(ct);
        if (deleted > 0)
            Logger.Info($"DataPurger: deleted {deleted} TAF record(s) older than {retentionDays} days.");
    }
}