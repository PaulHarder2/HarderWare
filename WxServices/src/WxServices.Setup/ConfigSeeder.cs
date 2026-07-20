using MetarParser.Data;
using MetarParser.Data.Configuration;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

namespace WxServices.Setup;

/// <summary>How a seed run resolved, per row — reported to the operator so a re-run is legible.</summary>
public sealed record SeedOutcome(int Inserted, int Updated, int Unchanged);

/// <summary>
/// Seeds the foundational configuration rows into the <c>Config</c> table (WX-314, AC-6), the
/// runtime single source of truth for application configuration (WX-307).
/// </summary>
public static class ConfigSeeder
{
    /// <summary>
    /// Upserts <paramref name="rows"/>. A row whose value already matches is left completely
    /// untouched — including its <c>UpdatedUtc</c>, which records the last <em>real</em> write —
    /// so re-running setup with the same answers changes nothing (AC-4 idempotency, DB side).
    ///
    /// <para>Refuses bootstrap-critical keys outright (<see cref="BootstrapKeys"/>): the DB
    /// provider ignores those on read, so seeding one would produce a row that looks configured
    /// but has no effect — the worst kind of configuration bug to diagnose.</para>
    /// </summary>
    public static async Task<SeedOutcome> UpsertAsync(
        WeatherDataContext db,
        IReadOnlyList<KeyValuePair<string, string?>> rows,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        foreach (var row in rows)
        {
            if (BootstrapKeys.IsBootstrapKey(row.Key))
                throw new SetupException(
                    $"Refusing to seed bootstrap-critical key '{row.Key}' into the Config table. " +
                    "Keys of this kind must stay in the per-environment appsettings.local.json — " +
                    "the configuration provider ignores them in the database, so the row would " +
                    "have no effect.");
        }

        // The existing-row snapshot below is taken once, so a key repeated within one batch would
        // take the insert path twice and fail on the primary key only at SaveChanges — after the
        // login, schema, and files have already been committed. Reject it up front instead.
        var duplicate = rows
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new SetupException(
                $"Duplicate configuration key '{duplicate.Key}' in the seed set — each key may appear once.");

        var keys = rows.Select(r => r.Key).ToArray();
        var existing = await db.Config
            .Where(c => keys.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, StringComparer.OrdinalIgnoreCase, ct);

        int inserted = 0, updated = 0, unchanged = 0;

        foreach (var (key, value) in rows)
        {
            if (!existing.TryGetValue(key, out var entry))
            {
                db.Config.Add(new Config { Key = key, Value = value, UpdatedUtc = utcNow });
                inserted++;
            }
            else if (!string.Equals(entry.Value, value, StringComparison.Ordinal))
            {
                entry.Value = value;
                entry.UpdatedUtc = utcNow;
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new SeedOutcome(inserted, updated, unchanged);
    }
}