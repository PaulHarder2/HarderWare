using System.Globalization;
using System.Text.Encodings.Web;
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
    /// <summary>
    /// Write options for every generated local.json. <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    /// is deliberate: the default encoder escapes non-ASCII and HTML-sensitive characters, so a Danish
    /// place name would be rewritten <c>København</c> → <c>København</c> and a password containing
    /// <c>&amp;</c>, <c>+</c> or <c>'</c> would come back <c>&</c>, <c>+</c>, <c>'</c>. That is
    /// semantically lossless — it reparses to identical values — but it hands the operator back *their own
    /// file* looking nothing like what they wrote, which is the same human-factors failure as destroying a
    /// key, only quieter. "Unsafe" names an HTML-injection risk that does not exist for a settings file
    /// never emitted into markup. (WX-326; found by review, measured not assumed.)
    /// </summary>
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
    /// The WxManager <c>local.json</c>. Unlike the container files this one has no committed
    /// <c>.example</c> — it is the operator's own file — so a re-run <b>merges</b> into whatever is
    /// already there (WX-326): <paramref name="existingJson"/> is preserved key-for-key and only
    /// <c>ConnectionStrings:WeatherData</c> is set. Pass null (no existing file) for the fresh-box
    /// path, which yields the connection string alone — the foundational fields (home / region /
    /// bbox / map-extent) are DB-seeded (WX-314), not written here.
    /// </summary>
    /// <exception cref="JsonException">
    /// <paramref name="existingJson"/> is not valid JSON. Deliberately not swallowed: overwriting a
    /// file we cannot read is exactly the data loss this method exists to prevent.
    /// <para>
    /// This is deliberately <b>stricter than the runtime</b>. The JSON configuration provider the
    /// services and WxManager read this file with accepts <c>//</c> comments and trailing commas
    /// (verified 2026-07-26), so a file that runs fine can fail here. Parsing leniently was
    /// considered and rejected: the merge re-serializes the document, and
    /// <c>System.Text.Json</c> cannot round-trip a comment — so "tolerant" would mean silently
    /// deleting the operator's annotations, which is the same data loss as WX-326 itself, only
    /// quieter. Failing loudly with an explanation is the honest trade.
    /// </para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="existingJson"/> is valid JSON but not an object; its <c>ConnectionStrings</c>
    /// member is not an object; or the document contains a <b>duplicate key</b> — <c>JsonNode.Parse</c>
    /// raises <c>ArgumentException</c> ("An item with the same key has already been added") rather than
    /// a <c>JsonException</c> for that case, which is easy to miss when writing the caller's filter.
    /// </exception>
    public static string BuildWxManagerLocalJson(string? existingJson, string connectionString)
    {
        var root = ParseExistingRoot(existingJson) ?? new JsonObject();

        // Match the operator's own casing rather than imposing ours. JSON is case-sensitive but the
        // .NET configuration binder is NOT, so a file written "connectionStrings" works today. An
        // ordinal lookup would miss it and ADD a second key differing only in case — and
        // JsonConfigurationFileParser folds keys with OrdinalIgnoreCase, so the provider then refuses
        // the whole file. Setup would report success and WxManager would fail to start afterwards,
        // pointing the operator at a file setup had just written. (WX-326, found by code review;
        // reproduced end-to-end before fixing.)
        var connectionsKey = ResolveKey(root, "ConnectionStrings", nameof(existingJson));

        // An explicit JSON null is treated exactly like an absent member, not as a wrong shape. The
        // configuration provider loads {"ConnectionStrings": null} without complaint, so rejecting it
        // would tell the operator their working file is malformed — and replacing it with a fresh
        // object loses nothing, because there was nothing there. Only a *non-null, non-object* value
        // (a string, a number, an array) is a genuine shape error worth stopping for.
        // (WX-326: an earlier cut conflated null with wrong-shape; caught by code review.)
        if (connectionsKey is null || root[connectionsKey] is null)
        {
            connectionsKey ??= "ConnectionStrings";
            root[connectionsKey] = new JsonObject();
        }
        else if (root[connectionsKey] is not JsonObject)
        {
            throw new ArgumentException(
                $"Existing '{connectionsKey}' is not a JSON object.", nameof(existingJson));
        }

        var connections = root[connectionsKey]!.AsObject();
        var weatherKey = ResolveKey(connections, "WeatherData", nameof(existingJson)) ?? "WeatherData";
        connections[weatherKey] = connectionString;

        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// Returns the member of <paramref name="obj"/> matching <paramref name="wanted"/> ignoring case —
    /// as the author actually spelled it — or null when absent.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The object already carries two members differing only in case. That file is *already* unloadable
    /// (the configuration provider folds them and rejects the duplicate), so we say so plainly instead
    /// of silently picking one and writing a file that still will not load.
    /// </exception>
    private static string? ResolveKey(JsonObject obj, string wanted, string paramName)
    {
        var matches = obj
            .Select(kvp => kvp.Key)
            .Where(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new ArgumentException(
                $"Existing file has {matches.Count} '{wanted}' members differing only in case " +
                $"({string.Join(", ", matches)}). The configuration provider treats these as one key " +
                "and refuses the file; remove the duplicates before re-running setup.", paramName),
        };
    }

    /// <summary>
    /// Parses an existing local.json to its root object, or null when there is no existing file
    /// (absent, empty, or whitespace) — the fresh-box path.
    /// </summary>
    private static JsonObject? ParseExistingRoot(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson))
            return null;

        var parsed = JsonNode.Parse(existingJson);
        return parsed switch
        {
            JsonObject obj => obj,
            null => null,
            _ => throw new ArgumentException(
                "Existing file is valid JSON but not a JSON object.", nameof(existingJson)),
        };
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