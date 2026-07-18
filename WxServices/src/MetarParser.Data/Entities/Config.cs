namespace MetarParser.Data.Entities;

/// <summary>
/// Key/value application-configuration table (WX-313).  Each row is one
/// configuration entry whose <see cref="Key"/> is a full configuration path in
/// the standard <c>Section:SubKey</c> convention (e.g. <c>Fetch:HomeIcao</c>),
/// so a DB-backed <c>IConfigurationProvider</c> can layer these values into
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> at exactly
/// the slot the equivalent JSON key would occupy.  This table is the runtime
/// single source of truth for application configuration (WX-307); it ships
/// empty and is populated by later subtasks.
/// <para>
/// Bootstrap-critical keys — the connection string, <c>Database:StartupRetry</c>
/// options, telemetry — are never stored here: they configure the very channel
/// this table is read through, so they must remain in the per-environment file.
/// </para>
/// </summary>
public class Config
{
    /// <summary>
    /// Primary key — the configuration path in <c>Section:SubKey</c> form
    /// (e.g. <c>Smtp:Host</c>).  A natural key; the configuration system and
    /// SQL Server's default collation both treat keys case-insensitively.
    /// </summary>
    public string Key { get; set; } = null!;

    /// <summary>The configuration value.  <c>null</c> represents a present-but-null entry.</summary>
    public string? Value { get; set; }

    /// <summary>UTC timestamp of the last write to this row.</summary>
    public DateTime UpdatedUtc { get; set; }
}