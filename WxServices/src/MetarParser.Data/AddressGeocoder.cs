using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Resolves a recipient's address-input string to geographic coordinates and a
/// human-readable locality name.  Three input forms are accepted, in this
/// detection order:
/// <list type="number">
///   <item><description><c>///word.word.word</c> — What3Words address</description></item>
///   <item><description><c>lat, lon</c> — decimal degrees, no API call</description></item>
///   <item><description>Anything else — passed to the Nominatim geocoder</description></item>
/// </list>
/// </summary>
public static class AddressGeocoder
{
    private const string NominatimBase = "https://nominatim.openstreetmap.org/search";

    // Three lowercase dot-separated words, optionally preceded by ///
    private static readonly Regex W3wPattern = new(
        @"^/{3}[a-z]+\.[a-z]+\.[a-z]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Whether <paramref name="address"/> is a What3Words address, by the same rule
    /// <see cref="LookupAsync"/> dispatches on. Exposed (WX-322) so a caller that must fetch the
    /// API key beforehand asks THIS question rather than re-implementing it — a caller-side
    /// "starts with ///" check silently diverges the moment this pattern changes, and the symptom
    /// would be "no API key set" for an address that has one.
    /// </summary>
    public static bool IsWhat3WordsAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && W3wPattern.IsMatch(address.Trim());

    // Two signed decimals separated by a comma — e.g. "30.07, -95.55"
    private static readonly Regex LatLonPattern = new(
        @"^\s*([-+]?\d+(?:\.\d+)?)\s*,\s*([-+]?\d+(?:\.\d+)?)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Resolves <paramref name="address"/> to (Latitude, Longitude, LocalityName)
    /// using whichever input form is detected.  Returns <see langword="null"/>
    /// if the address cannot be resolved by any path.
    /// </summary>
    /// <param name="address">A street address, <c>///word.word.word</c> What3Words address, or <c>lat, lon</c> decimal-degree pair.</param>
    /// <param name="httpClient">HTTP client for outbound geocoder requests (must allow outbound HTTPS).</param>
    /// <param name="w3wApiKey">A What3Words API key. May be null/empty when the input is not a What3Words address; sourcing it is the caller's business (WxManager reads it from <c>GlobalSettings</c>).</param>
    /// <returns>
    /// A tuple of (Latitude, Longitude, LocalityName) if resolved successfully,
    /// or <see langword="null"/> if the address cannot be geocoded.
    /// </returns>
    /// <sideeffects>
    /// May make an HTTP GET request to Nominatim or What3Words.
    /// Writes one INFO log line on entry naming the input and the chosen path,
    /// and one INFO line on success (with resolved coordinates and locality)
    /// or one WARN line on failure. Path-specific clients log their own
    /// detailed error context (HTTP status, W3W error code, etc.) on top of these.
    /// </sideeffects>
    public static async Task<(double Latitude, double Longitude, string LocalityName)?> LookupAsync(
        string address, HttpClient httpClient, string? w3wApiKey)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            Logger.Error("AddressGeocoder.LookupAsync called with null or empty address — returning null.");
            return null;
        }

        var trimmed = address.Trim();
        string path;
        (double Latitude, double Longitude, string LocalityName)? result;

        if (W3wPattern.IsMatch(trimmed))
        {
            path = "What3Words";
            Logger.Info($"AddressGeocoder: looking up '{trimmed}' via {path}.");
            if (string.IsNullOrWhiteSpace(w3wApiKey))
            {
                // Name the place the key actually lives and how to set it. The message used to
                // cite What3Words:ApiKey in appsettings.local.json, which since WX-322 is the wrong
                // place to look — and a wrong pointer costs more than no pointer.
                Logger.Error(
                    $"AddressGeocoder: '{trimmed}' supplied but no What3Words API key is set. " +
                    "The key lives in the GlobalSettings database row (column What3WordsApiKey); " +
                    "set it in WxManager → Configure → Address Lookup (What3Words). Returning null.");
                return null;
            }
            // The W3W SDK manages its own HttpClient; httpClient is only used by Nominatim.
            result = await What3WordsClient.LookupAsync(trimmed, w3wApiKey);
        }
        else if (LatLonPattern.Match(trimmed) is { Success: true } m)
        {
            path = "lat/lon";
            Logger.Info($"AddressGeocoder: looking up '{trimmed}' via {path}.");
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                Logger.Error($"AddressGeocoder: could not parse '{trimmed}' as lat,lon — returning null.");
                return null;
            }
            if (lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                Logger.Error($"AddressGeocoder: lat/lon out of range in '{trimmed}' (lat must be -90..90, lon -180..180) — returning null.");
                return null;
            }
            // Direct-entry path supplies no locality; the caller (WxManager) leaves
            // its Locality control untouched for the user to fill.
            result = (lat, lon, "");
        }
        else
        {
            path = "Nominatim";
            Logger.Info($"AddressGeocoder: looking up '{trimmed}' via {path}.");
            result = await LookupNominatimAsync(trimmed, httpClient);
        }

        if (result is { } r)
        {
            Logger.Info($"AddressGeocoder: resolved '{trimmed}' via {path} → ({r.Latitude:F4}, {r.Longitude:F4}) locality='{r.LocalityName}'.");
        }
        else
        {
            Logger.Warn($"AddressGeocoder: could not resolve '{trimmed}' via {path} — see prior log for cause.");
        }

        return result;
    }

    /// <summary>
    /// Geocodes a free-form street address via the Nominatim (OpenStreetMap) public API.
    /// </summary>
    private static async Task<(double Latitude, double Longitude, string LocalityName)?> LookupNominatimAsync(
        string address, HttpClient httpClient)
    {
        var url = $"{NominatimBase}?q={Uri.EscapeDataString(address)}&format=json&addressdetails=1&limit=1";

        NominatimResult[]? results;
        try
        {
            // Nominatim requires a User-Agent identifying the application.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "WxParser/1.0");
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            results = await response.Content.ReadFromJsonAsync<NominatimResult[]>();
        }
        catch (Exception ex)
        {
            Logger.Error($"Address geocoding failed: {ex.Message}");
            return null;
        }

        if (results is not { Length: > 0 })
        {
            Logger.Warn($"Address '{address}' could not be geocoded.");
            return null;
        }

        var r = results[0];
        var localityName = ResolveLocalityName(r.Address);

        if (!double.TryParse(r.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(r.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            Logger.Error($"Nominatim returned unparseable coordinates: lat='{r.Lat}' lon='{r.Lon}'");
            return null;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            Logger.Error($"Nominatim returned out-of-range coordinates: lat={lat}, lon={lon} — returning null.");
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
        [JsonPropertyName("lat")] public string Lat { get; set; } = "";
        [JsonPropertyName("lon")] public string Lon { get; set; } = "";
        [JsonPropertyName("address")] public NominatimAddress? Address { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("suburb")] public string? Suburb { get; set; }
        [JsonPropertyName("town")] public string? Town { get; set; }
        [JsonPropertyName("village")] public string? Village { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("county")] public string? County { get; set; }
    }
}