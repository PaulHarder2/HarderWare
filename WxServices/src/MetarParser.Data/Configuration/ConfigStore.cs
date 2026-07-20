using System.Globalization;

using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

namespace MetarParser.Data.Configuration;

/// <summary>
/// The operational settings WxManager's Configure tab owns (WX-315), and the rows it writes.
///
/// <para>Pure, and deliberately here rather than in the WxManager code-behind: WPF projects are
/// excluded from CI (WX-135), so mapping logic left in the tab could not be covered by a CI test.
/// Keeping it in the data layer makes the key names and the parsing rules testable.</para>
/// </summary>
public static class OperationalConfig
{
    public const string SmtpHostKey = "Smtp:Host";
    public const string SmtpPortKey = "Smtp:Port";
    public const string AlertEmailKey = "Monitor:AlertEmail";

    /// <summary>
    /// The rows for the freely-editable operational settings. Values are trimmed, and a non-empty
    /// port must be a valid TCP port — a bad one <em>throws</em> rather than silently substituting
    /// a default (the pre-WX-315 tab quietly wrote 587 for any unparseable entry, so a typo looked
    /// like it saved and changed nothing).
    ///
    /// <para>A blank field emits <b>no row</b>. Writing one would turn "unset, so the shared
    /// default applies" into an explicit database override that shadows
    /// <c>appsettings.shared.json</c> from then on — a value the operator never chose, and one that
    /// would make a later change to the shared default silently ineffective.</para>
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> BuildEditableRows(
        string smtpHost, string smtpPort, string alertEmail)
    {
        var rows = new List<KeyValuePair<string, string?>>();

        Add(SmtpHostKey, smtpHost);

        var port = (smtpPort ?? string.Empty).Trim();
        if (port.Length > 0)
        {
            if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1 || parsed > 65535)
            {
                throw new ConfigWriteException(
                    $"SMTP port '{smtpPort}' is not a valid port number (1-65535).");
            }

            rows.Add(new(SmtpPortKey, parsed.ToString(CultureInfo.InvariantCulture)));
        }

        Add(AlertEmailKey, alertEmail);
        return rows;

        void Add(string key, string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length > 0)
                rows.Add(new(key, trimmed));
        }
    }
}

/// <summary>Thrown when a write to the <c>Config</c> table is refused or malformed.</summary>
public sealed class ConfigWriteException : Exception
{
    public ConfigWriteException(string message) : base(message) { }
}

/// <summary>How an upsert resolved, per row — reported so a repeat write is legible.</summary>
public sealed record ConfigUpsertResult(int Inserted, int Updated, int Unchanged);

/// <summary>
/// The single write path into the <c>Config</c> table — the runtime source of truth for
/// application configuration (WX-307).
///
/// <para>Extracted here (WX-315) because there are now two writers: the setup console seeding a
/// fresh box (WX-314) and WxManager's Configure tab saving operational settings. Both need the
/// same upsert semantics <em>and</em> the same <see cref="BootstrapKeys"/> refusal, and a second
/// copy would be a second chance to drift — the same reasoning that put the key rule here.</para>
/// </summary>
public static class ConfigStore
{
    /// <summary>
    /// Upserts <paramref name="rows"/>. A row whose value already matches is left completely
    /// untouched — including its <c>UpdatedUtc</c>, which records the last <em>real</em> write — so
    /// re-saving unchanged settings changes nothing.
    ///
    /// <para>Refuses bootstrap-critical keys (<see cref="BootstrapKeys"/>): the configuration
    /// provider ignores those on read, so writing one would produce a row that looks configured
    /// but has no effect — the worst kind of configuration bug to diagnose. Also refuses a key
    /// repeated within one batch, which would otherwise take the insert path twice and fail on the
    /// primary key at <c>SaveChanges</c>, after other work had already committed.</para>
    /// </summary>
    public static async Task<ConfigUpsertResult> UpsertAsync(
        WeatherDataContext db,
        IReadOnlyList<KeyValuePair<string, string?>> rows,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        foreach (var row in rows)
        {
            if (BootstrapKeys.IsBootstrapKey(row.Key))
                throw new ConfigWriteException(
                    $"Refusing to write bootstrap-critical key '{row.Key}' to the Config table. " +
                    "Keys of this kind must stay in the per-environment appsettings.local.json — " +
                    "the configuration provider ignores them in the database, so the row would " +
                    "have no effect.");
        }

        var duplicate = rows
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ConfigWriteException(
                $"Duplicate configuration key '{duplicate.Key}' in the write set — each key may appear once.");

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
        return new ConfigUpsertResult(inserted, updated, unchanged);
    }
}