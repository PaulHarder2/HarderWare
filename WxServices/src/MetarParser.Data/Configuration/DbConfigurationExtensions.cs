using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MetarParser.Data.Configuration;

/// <summary>
/// Configuration-builder helper that overlays the <c>Config</c> database table
/// onto configuration (WX-313), the runtime single source of truth for
/// application config (WX-307).
/// </summary>
public static class DbConfigurationExtensions
{
    /// <summary>
    /// Connect-timeout cap (seconds) for the provider's own config read. A best-effort
    /// overlay must never freeze startup on an unreachable DB: WxManager reads this
    /// synchronously on its UI thread (the 15 s default would freeze the window), and in
    /// the services <c>EnsureSchemaAsync</c> owns the real DB-ready wait/retry — so the
    /// provider fails fast and the post-schema reload picks values up afterwards.
    /// </summary>
    private const int ConfigReadConnectTimeoutSeconds = 5;

    /// <summary>
    /// Appends the <c>Config</c>-table overlay, resolving the <c>WeatherData</c> connection
    /// string from the file layers already added (an interim build). Call this AFTER the JSON
    /// sources so the database wins (last source wins). This is the convenience form the host
    /// wiring uses; the database cannot supply the string used to reach it, hence the interim
    /// read of the file layers.
    /// </summary>
    /// <param name="builder">The configuration builder to append to.</param>
    public static IConfigurationBuilder AddDatabaseConfig(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The interim root is a throwaway used only to read the connection string; dispose it
        // so its (reloadOnChange) JSON file providers don't leak watchers past this call.
        var interim = builder.Build();
        string? connectionString;
        try
        {
            connectionString = interim.GetConnectionString("WeatherData");
        }
        finally
        {
            (interim as IDisposable)?.Dispose();
        }

        return builder.AddDatabaseConfig(connectionString);
    }

    /// <summary>
    /// Appends a <see cref="DbConfigurationProvider"/> reading the <c>Config</c> table over the
    /// given SQL Server connection string. Prefer the parameterless overload for host wiring;
    /// this explicit form exists for tests and callers that already hold the string. A null/blank
    /// string, or an unreachable database / not-yet-created table, contributes no keys (the
    /// provider's <see cref="DbConfigurationProvider.Load"/> is resilient).
    /// </summary>
    /// <param name="builder">The configuration builder to append to.</param>
    /// <param name="connectionString">The <c>WeatherData</c> connection string, resolved from the file layers.</param>
    public static IConfigurationBuilder AddDatabaseConfig(
        this IConfigurationBuilder builder, string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Func<WeatherDataContext>? contextFactory = null;
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var effective = WithConnectTimeoutCap(connectionString);
            contextFactory = () => new WeatherDataContext(
                new DbContextOptionsBuilder<WeatherDataContext>()
                    .UseSqlServer(effective)
                    .Options);
        }

        return builder.Add(new DbConfigurationSource(contextFactory));
    }

    /// <summary>
    /// Returns <paramref name="connectionString"/> with its connect timeout capped at
    /// <see cref="ConfigReadConnectTimeoutSeconds"/>. A malformed string can't be parsed here;
    /// it is returned unchanged so the provider's resilient <see cref="DbConfigurationProvider.Load"/>
    /// degrades to file configuration — a malformed string throws <c>ArgumentException</c> /
    /// <c>FormatException</c> when the connection is opened (not <c>DbException</c>), and
    /// <c>Load</c> catches all three — rather than aborting at wiring time.
    /// </summary>
    private static string WithConnectTimeoutCap(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = ConfigReadConnectTimeoutSeconds,
            }.ConnectionString;
        }
        catch (Exception)
        {
            return connectionString;
        }
    }
}