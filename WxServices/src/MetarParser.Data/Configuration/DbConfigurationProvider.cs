using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxServices.Logging;

namespace MetarParser.Data.Configuration;

/// <summary>
/// <see cref="IConfigurationProvider"/> that overlays application configuration
/// stored in the <c>Config</c> database table (WX-313) onto the file-based
/// configuration layers.  Each row's <c>Key</c> is a full configuration path in
/// the <c>Section:SubKey</c> convention, so values land at exactly the slot the
/// equivalent JSON key would occupy — transparently to every
/// <c>GetSection</c>/<c>Bind</c> call site.
/// <para>
/// Registered LAST in the configuration stack so the database wins over the JSON
/// files.  It reads through a short-lived <see cref="WeatherDataContext"/> (like
/// every other DB read site); it lives in this project rather than
/// <c>WxServices.Common</c> because <c>MetarParser.Data</c> already references
/// Common, so the reverse edge would be a project-reference cycle.
/// </para>
/// <para>
/// The context is supplied by a factory so the provider is DB-agnostic: production
/// wiring passes a SQL Server context (via <see cref="DbConfigurationExtensions.AddDatabaseConfig"/>),
/// while tests pass a SQLite in-memory context.
/// </para>
/// <para>
/// <see cref="Load"/> is deliberately resilient: an unreachable database or a
/// not-yet-created <c>Config</c> table (the fresh-DB case, before
/// <c>EnsureSchemaAsync</c> has run) contributes zero keys and never throws, so
/// the file-sourced connection string and retry options can still bootstrap the
/// process.  The host calls <see cref="IConfigurationRoot.Reload"/> immediately
/// after schema-ensure to re-run this load once the table is guaranteed present.
/// </para>
/// </summary>
internal sealed class DbConfigurationProvider : ConfigurationProvider
{
    private readonly Func<WeatherDataContext>? _contextFactory;

    /// <param name="contextFactory">
    /// Creates a short-lived <see cref="WeatherDataContext"/> to read from, or
    /// <c>null</c> when no connection string was available — in which case the
    /// provider contributes nothing.
    /// </param>
    public DbConfigurationProvider(Func<WeatherDataContext>? contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <inheritdoc />
    public override void Load()
    {
        // No connection string was available at wiring time: overlay nothing
        // rather than throw — the file layers still carry the bootstrap keys.
        if (_contextFactory is null)
        {
            Data = NewData();
            return;
        }

        try
        {
            using var db = _contextFactory();

            var data = NewData();
            foreach (var row in db.Config.AsNoTracking())
            {
                // Bootstrap-critical keys stay file-sourced — never taken from the DB
                // overlay (see IsBootstrapKey: BootstrapSectionPrefixes + ClaudeTimeoutKey).
                // Skip them so a stray Config row cannot override config the process
                // consumes BEFORE this load runs (or, for the connection string, the very
                // value used to reach the DB).
                if (IsBootstrapKey(row.Key))
                    continue;

                data[row.Key] = row.Value;
            }

            Data = data;
            Logger.Debug(
                $"DbConfigurationProvider loaded {data.Count} configuration key(s) from the Config table.");
        }
        catch (Exception ex) when (ex is DbException or ArgumentException or FormatException)
        {
            // Unreachable database, or the Config table doesn't exist yet — the
            // fresh-DB case before EnsureSchemaAsync has run.  SQL Server raises
            // SqlException and SQLite raises SqliteException (both DbException).  A
            // malformed connection string that WithConnectTimeoutCap could not parse
            // is passed through unchanged and surfaces HERE as ArgumentException /
            // FormatException on connect (not DbException), so we catch those too —
            // a bad file value must degrade to file configuration, never abort
            // startup (for the services this Load runs during host .Build(), outside
            // the startup try/catch).  Overlay nothing and never break bootstrap; the
            // host reloads configuration right after schema-ensure to re-run this.
            Data = NewData();
            Logger.Warn(
                "DbConfigurationProvider could not read the Config table " +
                $"({ex.GetBaseException().Message.TrimEnd('.')}); continuing with file configuration only.");
        }
    }

    private static Dictionary<string, string?> NewData() =>
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a key stays file-sourced and is never overlaid from the DB. The rule itself lives in
    /// <see cref="BootstrapKeys"/> (extracted in WX-314) so this read-side guard — the belt — and the
    /// write-side guards in the setup console and WX-315's Configure tab — the suspenders — cannot drift apart.
    /// </summary>
    private static bool IsBootstrapKey(string key) => BootstrapKeys.IsBootstrapKey(key);
}