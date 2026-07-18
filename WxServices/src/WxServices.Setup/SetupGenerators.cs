using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.SqlClient;

namespace WxServices.Setup;

/// <summary>Builds the SQL Server connection strings the setup script writes (WX-314). Pure — unit-tested.</summary>
public static class ConnectionStrings
{
    /// <summary>
    /// Container connection string: SQL authentication over the host.docker.internal TCP route
    /// (a Linux container has no Windows identity, so Trusted auth can't work). The server is
    /// always <c>host.docker.internal,1433</c> — the container→host route — not the <c>--server</c>
    /// used for the script's own / WxManager's native connection. Built via
    /// <see cref="SqlConnectionStringBuilder"/> so a prompted password containing <c>;</c>, <c>=</c>,
    /// or quotes is escaped correctly rather than breaking (or injecting into) the string.
    /// </summary>
    public static string BuildContainer(string database, string sqlLogin, string password) =>
        new SqlConnectionStringBuilder
        {
            DataSource = "host.docker.internal,1433",
            InitialCatalog = database,
            UserID = sqlLogin,
            Password = password,
            TrustServerCertificate = true,
        }.ConnectionString;

    /// <summary>WxManager (native) connection string: Windows Trusted auth, no password.</summary>
    public static string BuildWxManager(string server, string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
        }.ConnectionString;
}

/// <summary>Builds the per-environment <c>appsettings.local.json</c> contents (WX-314). Pure — unit-tested.</summary>
public static class LocalJsonGenerator
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// A container <c>local.json</c>: the committed <c>.example</c> template with only
    /// <c>ConnectionStrings:WeatherData</c> replaced by <paramref name="connectionString"/> —
    /// the <c>_README</c> docs, <c>Telemetry</c>, and per-service extras are preserved.
    /// </summary>
    public static string BuildContainerLocalJson(string exampleJson, string connectionString)
    {
        var root = JsonNode.Parse(exampleJson)
            ?? throw new ArgumentException("Template JSON parsed to null.", nameof(exampleJson));
        var connections = root["ConnectionStrings"]?.AsObject()
            ?? throw new ArgumentException("Template has no ConnectionStrings object.", nameof(exampleJson));
        connections["WeatherData"] = connectionString;
        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// The WxManager <c>local.json</c>: only the connection string. The foundational fields
    /// (home / region / bbox / map-extent) are now DB-seeded (WX-314), not written to file.
    /// </summary>
    public static string BuildWxManagerLocalJson(string connectionString)
    {
        var root = new JsonObject
        {
            ["ConnectionStrings"] = new JsonObject { ["WeatherData"] = connectionString },
        };
        return root.ToJsonString(Indented);
    }
}

/// <summary>The foundational location values the setup script prompts for and seeds into the <c>Config</c> table.</summary>
public sealed record FoundationalInputs(
    string HomeIcao,
    double HomeLatitude,
    double HomeLongitude,
    double BoundingBoxDegrees,
    double RegionSouth,
    double RegionNorth,
    double RegionWest,
    double RegionEast,
    string MapExtent);

/// <summary>Builds the foundational <c>Config</c>-table seed rows (WX-314). Pure — unit-tested.</summary>
public static class ConfigSeed
{
    /// <summary>
    /// The <c>Section:SubKey</c> rows for the foundational fields. Never emits a bootstrap-critical
    /// key (<c>ConnectionStrings:</c> / <c>Database:StartupRetry:</c> / <c>Telemetry:</c> /
    /// <c>Claude:TimeoutSeconds</c>) — those stay file-sourced and the provider would ignore them.
    /// Numbers use the invariant culture so the seeded text is stable across locales.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> BuildFoundationalSeedRows(FoundationalInputs inputs)
    {
        static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

        return new List<KeyValuePair<string, string?>>
        {
            new("Fetch:HomeIcao", inputs.HomeIcao),
            new("Fetch:HomeLatitude", Num(inputs.HomeLatitude)),
            new("Fetch:HomeLongitude", Num(inputs.HomeLongitude)),
            new("Fetch:BoundingBoxDegrees", Num(inputs.BoundingBoxDegrees)),
            new("Fetch:RegionSouth", Num(inputs.RegionSouth)),
            new("Fetch:RegionNorth", Num(inputs.RegionNorth)),
            new("Fetch:RegionWest", Num(inputs.RegionWest)),
            new("Fetch:RegionEast", Num(inputs.RegionEast)),
            new("WxVis:MapExtent", inputs.MapExtent),
        };
    }
}