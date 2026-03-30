using System.Text.Json.Serialization;
using System.Net.Http.Json;
using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Queries the Aviation Weather Center REST API to resolve an ICAO airport
/// identifier to its geographic coordinates.
/// </summary>
public static class AirportLocator
{
    private const string AirportApiBase = "https://aviationweather.gov/api/data/airport";

    /// <summary>
    /// Looks up the latitude and longitude of the airport identified by
    /// <paramref name="icao"/> using the Aviation Weather Center airport endpoint.
    /// </summary>
    /// <param name="icao">ICAO airport identifier to resolve (e.g. <c>"KDWH"</c>).</param>
    /// <param name="httpClient">HTTP client for the AWC airport API request.</param>
    /// <returns>
    /// A (Latitude, Longitude) tuple if the airport is found, or <see langword="null"/>
    /// if the identifier is unknown or the API call fails.
    /// </returns>
    /// <sideeffects>Makes an HTTP GET request to the Aviation Weather Center airport API. Writes error log entries on failure.</sideeffects>
    public static async Task<(double Latitude, double Longitude)?> LookupAsync(
        string icao, HttpClient httpClient)
    {
        if (string.IsNullOrWhiteSpace(icao))
        {
            Logger.Error("AirportLocator.LookupAsync called with null or empty ICAO — returning null.");
            return null;
        }

        var url = $"{AirportApiBase}?ids={Uri.EscapeDataString(icao)}&format=json";

        AirportDto[]? airports;
        try
        {
            airports = await httpClient.GetFromJsonAsync<AirportDto[]>(url);
        }
        catch (Exception ex)
        {
            Logger.Error($"Airport lookup failed: {ex.Message}");
            return null;
        }

        if (airports is not { Length: > 0 })
        {
            Logger.Warn($"Airport '{icao}' was not found in the Aviation Weather Center database.");
            return null;
        }

        return (airports[0].Lat, airports[0].Lon);
    }

    /// <summary>
    /// Finds the ICAO identifier of the nearest METAR station to the given
    /// coordinates by querying the Aviation Weather Center METAR endpoint with
    /// a bounding box in JSON format.
    /// Tries a 2-degree box first, widening to 5 degrees if no results are found.
    /// </summary>
    /// <param name="lat">Target latitude in decimal degrees.</param>
    /// <param name="lon">Target longitude in decimal degrees.</param>
    /// <param name="httpClient">HTTP client for the AWC METAR bounding-box API requests.</param>
    /// <returns>
    /// The ICAO identifier of the nearest METAR station, or <see langword="null"/>
    /// if no stations are found even within the 5-degree fallback box, or if the API call fails.
    /// </returns>
    /// <sideeffects>Makes up to two HTTP GET requests to the Aviation Weather Center METAR API. Writes error log entries on failure.</sideeffects>
    public static async Task<string?> FindNearestStationAsync(
        double lat, double lon, HttpClient httpClient)
    {
        foreach (var deg in new[] { 2.0, 5.0 })
        {
            var bbox = $"{lat - deg},{lon - deg},{lat + deg},{lon + deg}";
            var url  = $"https://aviationweather.gov/api/data/metar?bbox={bbox}&hours=1&format=json";

            MetarStationDto[]? stations;
            try
            {
                stations = await httpClient.GetFromJsonAsync<MetarStationDto[]>(url);
            }
            catch (Exception ex)
            {
                Logger.Error($"Nearest station lookup failed: {ex.Message}");
                return null;
            }

            if (stations is not { Length: > 0 }) continue;

            // Pick the station with the smallest squared Euclidean distance.
            return stations
                .OrderBy(s => Math.Pow(s.Lat - lat, 2) + Math.Pow(s.Lon - lon, 2))
                .First()
                .StationId;
        }

        Logger.Warn("No METAR stations found near the specified coordinates.");
        return null;
    }

    private sealed class AirportDto
    {
        [JsonPropertyName("icaoId")] public string IcaoId { get; set; } = "";
        [JsonPropertyName("lat")]    public double Lat    { get; set; }
        [JsonPropertyName("lon")]    public double Lon    { get; set; }
    }

    private sealed class MetarStationDto
    {
        [JsonPropertyName("icaoId")] public string StationId { get; set; } = "";
        [JsonPropertyName("lat")]    public double Lat       { get; set; }
        [JsonPropertyName("lon")]    public double Lon       { get; set; }
    }
}
