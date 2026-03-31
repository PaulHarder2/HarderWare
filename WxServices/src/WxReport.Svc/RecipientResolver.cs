using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using WxInterp;
using WxServices.Logging;

namespace WxReport.Svc;

/// <summary>
/// Resolves a recipient's address to geographic coordinates and the nearest
/// METAR and TAF station ICAOs, then caches the results back to
/// appsettings.local.json so subsequent service starts skip the API calls.
/// </summary>
public sealed class RecipientResolver
{
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient                           _httpClient;

    /// <summary>Initializes a new instance of <see cref="RecipientResolver"/> with the given dependencies.</summary>
    /// <param name="dbOptions">EF Core options used to query the database for nearby stations.</param>
    /// <param name="httpClient">HTTP client used for address geocoding and airport coordinate lookups.</param>
    public RecipientResolver(
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        _dbOptions  = dbOptions;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Ensures <paramref name="recipient"/> has resolved coordinates and station
    /// ICAOs.  If any cached values are missing the method geocodes the address,
    /// queries the database for the nearest stations, and writes the results back
    /// to appsettings.local.json.
    /// Returns <see langword="false"/> if resolution fails and the recipient
    /// cannot be used for report generation.
    /// </summary>
    /// <param name="recipient">
    /// The recipient config to resolve in place.  Latitude, longitude, MetarIcao, TafIcao,
    /// and LocalityName fields are populated on this object as a side effect.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if all required fields are populated (either pre-cached or
    /// newly resolved); <see langword="false"/> if geocoding or station lookup fails.
    /// </returns>
    /// <sideeffects>
    /// May mutate <paramref name="recipient"/>'s Latitude, Longitude, LocalityName, MetarIcao, and TafIcao fields.
    /// If any field was newly resolved, writes the updated values back to <c>appsettings.local.json</c>.
    /// Makes HTTP calls to the Nominatim geocoding API and AWC airport API.
    /// Writes log entries for each resolution step.
    /// </sideeffects>
    public async Task<bool> EnsureResolvedAsync(RecipientConfig recipient)
    {
        // .NET's JSON config provider maps JSON null to ""; normalize so null
        // checks below work correctly regardless of how the config was written.
        if (string.IsNullOrWhiteSpace(recipient.Id))           recipient.Id           = null;
        if (string.IsNullOrWhiteSpace(recipient.MetarIcao))    recipient.MetarIcao    = null;
        if (string.IsNullOrWhiteSpace(recipient.TafIcao))      recipient.TafIcao      = null;
        if (string.IsNullOrWhiteSpace(recipient.Address))      recipient.Address      = null;
        if (string.IsNullOrWhiteSpace(recipient.LocalityName)) recipient.LocalityName = null;

        if (recipient.MetarIcao is not null && recipient.TafIcao is not null)
            return true;

        var label   = recipient.Id ?? recipient.Email;
        var changed = false;

        if (recipient.Latitude is null || recipient.Longitude is null)
        {
            if (string.IsNullOrWhiteSpace(recipient.Address))
            {
                Logger.Warn($"{label}: no address and no cached coordinates — skipping.");
                return false;
            }

            Logger.Info($"{label}: geocoding address...");
            var geo = await RetryAsync(
                () => AddressGeocoder.LookupAsync(recipient.Address, _httpClient),
                maxAttempts: 3, delay: TimeSpan.FromSeconds(2),
                onRetry: attempt => Logger.Warn($"{label}: geocoding attempt {attempt} failed — retrying..."));

            if (geo is null)
            {
                Logger.Error($"{label}: geocoding failed after all attempts — skipping.");
                return false;
            }

            recipient.Latitude  = geo.Value.Latitude;
            recipient.Longitude = geo.Value.Longitude;

            if (string.IsNullOrWhiteSpace(recipient.LocalityName))
                recipient.LocalityName = geo.Value.LocalityName;

            Logger.Info($"{label}: resolved to lat={recipient.Latitude:F4} " +
                        $"lon={recipient.Longitude:F4} locality={recipient.LocalityName}");
            changed = true;
        }

        if (recipient.MetarIcao is null)
        {
            Logger.Info($"{label}: finding nearest METAR station...");

            // Only consider stations the local METAR fetcher is already collecting,
            // so the resolved ICAO is guaranteed to have data in the DB.
            var activeMetarStations = await GetActiveMetarStationsAsync();

            recipient.MetarIcao = await AirportLocator.FindNearestStationAsync(
                recipient.Latitude!.Value, recipient.Longitude!.Value,
                _httpClient, allowedIcaos: activeMetarStations);

            if (recipient.MetarIcao is null)
            {
                Logger.Error($"{label}: no METAR station found — skipping.");
                return false;
            }

            Logger.Info($"{label}: nearest METAR = {recipient.MetarIcao}");
            changed = true;
        }

        if (recipient.TafIcao is null)
        {
            Logger.Info($"{label}: finding nearest TAF station...");
            var tafIcao = await AirportLocator.FindNearestTafStationAsync(
                recipient.Latitude!.Value, recipient.Longitude!.Value, _httpClient);

            if (tafIcao is null)
            {
                Logger.Warn($"{label}: no TAF station found — forecast will be omitted.");
                recipient.TafIcao = "NONE";  // sentinel: lookup was attempted, nothing found
            }
            else
            {
                Logger.Info($"{label}: nearest TAF = {tafIcao}");
                recipient.TafIcao = tafIcao;
            }

            changed = true;
        }

        if (changed)
            SaveRecipient(recipient);

        return true;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the set of METAR station ICAOs that have observations in the local
    /// database within the last three hours.  Used to constrain station resolution
    /// to stations the METAR fetcher is actively collecting.
    /// Returns an empty set when the database has no recent data (e.g. first run),
    /// causing the resolver to fall back to the full AWC API result.
    /// </summary>
    /// <returns>A read-only set of active ICAO identifiers.</returns>
    private async Task<IReadOnlySet<string>> GetActiveMetarStationsAsync()
    {
        var cutoff = DateTime.UtcNow.AddHours(-3);
        await using var ctx = new WeatherDataContext(_dbOptions);
        var stations = await ctx.Metars
            .Where(m => m.ObservationUtc >= cutoff)
            .Select(m => m.StationIcao)
            .Distinct()
            .ToListAsync();
        return stations.ToHashSet();
    }

    // ── retry ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls <paramref name="operation"/> up to <paramref name="maxAttempts"/> times,
    /// returning the first non-null result.  Waits <paramref name="delay"/> between
    /// attempts and calls <paramref name="onRetry"/> with the 1-based attempt number
    /// before each retry.  Returns <see langword="null"/> if all attempts return null
    /// or throw.
    /// </summary>
    /// <typeparam name="T">The non-nullable result type returned by the operation.</typeparam>
    /// <param name="operation">The async operation to attempt.  A null return is treated as failure.</param>
    /// <param name="maxAttempts">Maximum number of attempts (must be ≥ 1).</param>
    /// <param name="delay">Time to wait between attempts.</param>
    /// <param name="onRetry">Called with the just-completed attempt number before sleeping and retrying.</param>
    /// <returns>The first non-null result, or <see langword="null"/> if all attempts fail.</returns>
    private static async Task<T?> RetryAsync<T>(
        Func<Task<T?>> operation, int maxAttempts, TimeSpan delay, Action<int> onRetry)
        where T : struct
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            T? result = null;
            try   { result = await operation(); }
            catch { /* logged by the callee; treat as null */ }

            if (result is not null) return result;
            if (attempt < maxAttempts)
            {
                onRetry(attempt);
                await Task.Delay(delay);
            }
        }
        return null;
    }

    // ── save-back ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the resolved location fields of <paramref name="recipient"/> back to
    /// <c>appsettings.local.json</c>.  Matches the recipient's entry in the JSON
    /// array by <c>Id</c> (preferred) or <c>Email</c>.  Logs a warning if no
    /// matching entry is found — this can happen if the config was modified externally.
    /// </summary>
    /// <param name="recipient">Recipient whose resolved fields should be persisted.</param>
    /// <sideeffects>
    /// Reads and overwrites <c>appsettings.local.json</c> alongside the executable.
    /// Writes a log entry on success or when the recipient cannot be located in the file.
    /// </sideeffects>
    private static void SaveRecipient(RecipientConfig recipient)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");

        JsonNode root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject()
            : new JsonObject();

        if (root["Report"] is not JsonObject reportNode)
        {
            reportNode = new JsonObject();
            root["Report"] = reportNode;
        }

        if (reportNode["Recipients"] is not JsonArray recipientsArray)
        {
            recipientsArray = new JsonArray();
            reportNode["Recipients"] = recipientsArray;
        }

        // Match on Id if present, fall back to Email for configs that predate this field.
        var matchCount = 0;
        foreach (var item in recipientsArray)
        {
            var matches = recipient.Id is not null
                ? item?["Id"]?.GetValue<string>()    == recipient.Id
                : item?["Email"]?.GetValue<string>() == recipient.Email;

            if (!matches) continue;

            item!["Latitude"]  = recipient.Latitude;
            item["Longitude"]  = recipient.Longitude;
            item["MetarIcao"]  = recipient.MetarIcao;
            item["TafIcao"]    = recipient.TafIcao;

            if (!string.IsNullOrWhiteSpace(recipient.LocalityName))
                item["LocalityName"] = recipient.LocalityName;

            matchCount++;
        }

        var label = recipient.Id ?? recipient.Email;

        if (matchCount > 0)
        {
            File.WriteAllText(path, root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
            Logger.Info($"{label}: resolved data cached to local settings.");
        }
        else
        {
            Logger.Warn($"{label}: not found in appsettings.local.json — cannot cache resolved data.");
        }
    }
}
