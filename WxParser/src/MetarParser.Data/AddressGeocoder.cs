using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace MetarParser.Data;

/// <summary>
/// Resolves a street address to geographic coordinates and a human-readable
/// locality name using the Nominatim geocoding API (OpenStreetMap).
/// </summary>
public static class AddressGeocoder
{
    private const string NominatimBase = "https://nominatim.openstreetmap.org/search";

    /// <summary>
    /// Geocodes <paramref name="address"/> and returns the latitude, longitude,
    /// and a short locality name suitable for use in weather reports
    /// (e.g. "Spring" for an address in Spring, TX).
    /// Returns <see langword="null"/> if the address cannot be resolved.
    /// </summary>
    public static async Task<(double Latitude, double Longitude, string LocalityName)?> LookupAsync(
        string address, HttpClient httpClient)
    {
        var url = $"{NominatimBase}?q={Uri.EscapeDataString(address)}&format=json&addressdetails=1&limit=1";

        NominatimResult[]? results;
        try
        {
            // Nominatim requires a User-Agent identifying the application.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "WxParser/1.0");
            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            results = await response.Content.ReadFromJsonAsync<NominatimResult[]>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Address geocoding failed: {ex.Message}");
            return null;
        }

        if (results is not { Length: > 0 })
        {
            Console.Error.WriteLine($"Address '{address}' could not be geocoded.");
            return null;
        }

        var r            = results[0];
        var localityName = ResolveLocalityName(r.Address);

        if (!double.TryParse(r.Lat, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(r.Lon, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            Console.Error.WriteLine($"Nominatim returned unparseable coordinates: lat='{r.Lat}' lon='{r.Lon}'");
            return null;
        }

        return (lat, lon, localityName);
    }

    /// <summary>
    /// Extracts the most specific meaningful locality name from a Nominatim
    /// address object, falling back through progressively broader levels.
    /// </summary>
    private static string ResolveLocalityName(NominatimAddress? addr)
    {
        if (addr is null) return "Unknown";
        return addr.Suburb
            ?? addr.Town
            ?? addr.Village
            ?? addr.City
            ?? addr.County
            ?? "Unknown";
    }

    // ── Nominatim response DTOs ───────────────────────────────────────────────

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")]     public string Lat { get; set; } = "";
        [JsonPropertyName("lon")]     public string Lon { get; set; } = "";
        [JsonPropertyName("address")] public NominatimAddress? Address { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("suburb")]  public string? Suburb  { get; set; }
        [JsonPropertyName("town")]    public string? Town    { get; set; }
        [JsonPropertyName("village")] public string? Village { get; set; }
        [JsonPropertyName("city")]    public string? City    { get; set; }
        [JsonPropertyName("county")]  public string? County  { get; set; }
    }
}
