using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxInterp;

using WxServices.Logging;

namespace WxReport.Svc;

/// <summary>
/// Resolves a recipient's address to geographic coordinates and the nearest
/// METAR and TAF station ICAOs.  Results are stored in the database
/// <c>Recipients</c> table so subsequent service starts skip the API calls.
/// </summary>
public sealed class RecipientResolver
{
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new instance of <see cref="RecipientResolver"/> with the given dependencies.</summary>
    /// <param name="dbOptions">EF Core options used to query the database for nearby stations.</param>
    /// <param name="httpClient">HTTP client used for address geocoding and airport coordinate lookups.</param>
    public RecipientResolver(
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        _dbOptions = dbOptions;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Ensures <paramref name="recipient"/> has resolved coordinates and station
    /// ICAOs.  If any cached values are missing the method geocodes the address
    /// and queries the database for the nearest stations.
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
    /// If any field was newly resolved, the caller persists the updated values to the database.
    /// Makes HTTP calls to the Nominatim geocoding API and AWC airport API.
    /// Writes log entries for each resolution step.
    /// </sideeffects>
    public async Task<bool> EnsureResolvedAsync(RecipientConfig recipient)
    {
        // .NET's JSON config provider maps JSON null to ""; normalize so null
        // checks below work correctly regardless of how the config was written.
        if (string.IsNullOrWhiteSpace(recipient.Id)) recipient.Id = null;
        if (string.IsNullOrWhiteSpace(recipient.MetarIcao)) recipient.MetarIcao = null;
        if (string.IsNullOrWhiteSpace(recipient.TafIcao)) recipient.TafIcao = null;
        if (string.IsNullOrWhiteSpace(recipient.Address)) recipient.Address = null;
        if (string.IsNullOrWhiteSpace(recipient.LocalityName)) recipient.LocalityName = null;

        if (recipient.MetarIcao is not null && recipient.TafIcao is not null)
            return true;

        var label = recipient.Id ?? recipient.Email;
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

            recipient.Latitude = geo.Value.Latitude;
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
            await SaveRecipientAsync(recipient);

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
            try { result = await operation(); }
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
    /// Persists the resolved location fields of <paramref name="recipient"/> to the
    /// <c>Recipients</c> database table.  Matches by <see cref="RecipientConfig.Id"/>.
    /// Logs a warning if no matching row is found — this should not happen in normal
    /// operation because recipients are seeded from config on first startup.
    /// </summary>
    /// <param name="recipient">Recipient whose resolved fields should be persisted.</param>
    /// <sideeffects>
    /// Opens a short-lived <see cref="WeatherDataContext"/> and updates the matching
    /// <c>Recipients</c> row.  Writes a log entry on success or on lookup failure.
    /// </sideeffects>
    private async Task SaveRecipientAsync(RecipientConfig recipient)
    {
        var label = recipient.Id ?? recipient.Email;

        await using var ctx = new WeatherDataContext(_dbOptions);
        var row = await ctx.Recipients
            .FirstOrDefaultAsync(r => r.RecipientId == recipient.Id);

        if (row is null)
        {
            Logger.Warn($"{label}: not found in Recipients table — cannot cache resolved data.");
            return;
        }

        row.Latitude = recipient.Latitude;
        row.Longitude = recipient.Longitude;
        row.MetarIcao = recipient.MetarIcao;
        row.TafIcao = recipient.TafIcao;

        if (!string.IsNullOrWhiteSpace(recipient.LocalityName))
            row.LocalityName = recipient.LocalityName;

        await ctx.SaveChangesAsync();
        Logger.Info($"{label}: resolved data cached to database.");
    }
}