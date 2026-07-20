using MetarParser.Data;
using MetarParser.Data.Configuration;

namespace WxServices.Setup;

/// <summary>How a seed run resolved, per row — reported to the operator so a re-run is legible.</summary>
public sealed record SeedOutcome(int Inserted, int Updated, int Unchanged);

/// <summary>
/// Seeds the foundational configuration rows into the <c>Config</c> table (WX-314, AC-6), the
/// runtime single source of truth for application configuration (WX-307).
///
/// <para>The upsert itself — including the bootstrap-key and duplicate-key refusals — lives in
/// <see cref="ConfigStore"/> (WX-315), shared with WxManager's Configure tab, which is the other
/// writer. This type only adapts it to the setup console's error contract.</para>
/// </summary>
public static class ConfigSeeder
{
    /// <summary>
    /// Upserts <paramref name="rows"/> via <see cref="ConfigStore"/>, translating a refusal into a
    /// <see cref="SetupException"/> so it prints as a plain actionable message rather than a stack trace.
    /// </summary>
    public static async Task<SeedOutcome> UpsertAsync(
        WeatherDataContext db,
        IReadOnlyList<KeyValuePair<string, string?>> rows,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        try
        {
            var result = await ConfigStore.UpsertAsync(db, rows, utcNow, ct);
            return new SeedOutcome(result.Inserted, result.Updated, result.Unchanged);
        }
        catch (ConfigWriteException ex)
        {
            throw new SetupException(ex.Message, ex);
        }
    }
}