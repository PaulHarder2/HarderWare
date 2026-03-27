using MetarParser.Data;
using WxParser.Logging;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace WxParser.Svc;

/// <summary>
/// Background worker that periodically fetches METAR and TAF reports from the
/// Aviation Weather Center API and stores new records in the database.
/// </summary>
public sealed class FetchWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient _http = new();

    public FetchWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config    = config;
        _dbOptions = dbOptions;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("FetchWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await FetchCycleAsync(stoppingToken);

            var intervalMinutes = int.TryParse(_config["Fetch:IntervalMinutes"], out var m) ? m : 10;
            Logger.Info($"Next fetch in {intervalMinutes} minute(s).");

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Logger.Info("FetchWorker stopped.");
    }

    private async Task FetchCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var homeIcao = _config["Fetch:HomeIcao"];
            var boxDeg   = double.TryParse(_config["Fetch:BoundingBoxDegrees"], out var bd) ? bd : 5.0;

            double? homeLat = double.TryParse(_config["Fetch:HomeLatitude"],  out var la) ? la : null;
            double? homeLon = double.TryParse(_config["Fetch:HomeLongitude"], out var lo) ? lo : null;

            if (homeLat is null || homeLon is null)
            {
                var homeAddress = _config["Fetch:HomeAddress"];

                if (!string.IsNullOrWhiteSpace(homeAddress))
                {
                    Logger.Info($"Coordinates not cached. Geocoding home address via Nominatim...");
                    var result = await AddressGeocoder.LookupAsync(homeAddress, _http);
                    if (result is null)
                    {
                        Logger.Error("Address geocoding failed. Skipping fetch cycle.");
                        return;
                    }
                    (homeLat, homeLon) = (result.Value.Latitude, result.Value.Longitude);
                    Logger.Info($"Resolved address: lat={homeLat:F4}  lon={homeLon:F4}  locality={result.Value.LocalityName}");
                    SaveLocalCoordinates(homeLat.Value, homeLon.Value, result.Value.LocalityName);
                }
                else if (!string.IsNullOrWhiteSpace(homeIcao))
                {
                    Logger.Info($"Coordinates for {homeIcao} not found in configuration. Looking up...");
                    var coords = await AirportLocator.LookupAsync(homeIcao, _http);
                    if (coords is null)
                    {
                        Logger.Error($"Airport lookup for '{homeIcao}' failed. Skipping fetch cycle.");
                        return;
                    }
                    (homeLat, homeLon) = coords.Value;
                    Logger.Info($"Resolved {homeIcao}: lat={homeLat:F4}  lon={homeLon:F4}");
                    SaveLocalCoordinates(homeLat.Value, homeLon.Value, localityName: null);
                }
                else
                {
                    Logger.Error("No HomeAddress or HomeIcao configured. Skipping fetch cycle.");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(homeIcao))
            {
                Logger.Info("HomeIcao not configured. Finding nearest METAR station...");
                homeIcao = await AirportLocator.FindNearestStationAsync(homeLat.Value, homeLon.Value, _http);
                if (homeIcao is not null)
                {
                    Logger.Info($"Nearest METAR station: {homeIcao}");
                    SaveLocalHomeIcao(homeIcao);
                }
                else
                {
                    Logger.Warn("Could not determine nearest METAR station. Home station fetch will be skipped.");
                }
            }

            Logger.Info($"Starting fetch cycle for {homeIcao ?? "unknown"} (bbox ±{boxDeg}°).");

            await MetarFetcher.FetchAndInsertAsync(homeLat.Value, homeLon.Value, boxDeg, _dbOptions, _http);
            // Fetch the home station explicitly in case it is omitted from the bounding box results.
            if (!string.IsNullOrWhiteSpace(homeIcao))
                await MetarFetcher.FetchAndInsertByStationAsync(homeIcao, _dbOptions, _http);
            await TafFetcher.FetchAndInsertAsync(homeLat.Value, homeLon.Value, boxDeg, _dbOptions, _http);

            Logger.Info("Fetch cycle complete.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Error("Unhandled exception in fetch cycle.", ex);
        }
    }

    /// <summary>
    /// Persists resolved airport coordinates to <c>appsettings.local.json</c> so
    /// subsequent service restarts do not need to call the airport API again.
    /// </summary>
    private void SaveLocalHomeIcao(string homeIcao)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
        JsonNode root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject()
            : new JsonObject();

        if (root["Fetch"] is not JsonObject fetch)
        {
            fetch = new JsonObject();
            root["Fetch"] = fetch;
        }

        fetch["HomeIcao"] = homeIcao;

        File.WriteAllText(path, root.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Logger.Info($"HomeIcao '{homeIcao}' saved to local settings.");
    }

    private void SaveLocalCoordinates(double lat, double lon, string? localityName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");

        JsonNode root;
        if (File.Exists(path))
            root = JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject();
        else
            root = new JsonObject();

        if (root["Fetch"] is not JsonObject fetch)
        {
            fetch = new JsonObject();
            root["Fetch"] = fetch;
        }

        fetch["HomeLatitude"]  = lat;
        fetch["HomeLongitude"] = lon;
        if (localityName is not null)
            fetch["HomeLocationName"] = localityName;

        File.WriteAllText(path, root.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Logger.Info($"Coordinates saved to {path}");
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
