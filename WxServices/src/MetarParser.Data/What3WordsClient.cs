using what3words.dotnet.wrapper;

using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Resolves a What3Words address (three dot-separated words) to geographic
/// coordinates and a nearest-place locality string via the public What3Words API,
/// using the official <c>what3words.dotnet.wrapper</c> NuGet package.
/// </summary>
public static class What3WordsClient
{
    /// <summary>
    /// Geocodes the three-word address <paramref name="words"/> and returns the
    /// latitude, longitude, and a short nearest-place name suitable for use in
    /// weather reports.  Returns <see langword="null"/> on any failure (network,
    /// API error, missing API key, or unparseable response).
    /// </summary>
    /// <param name="words">
    /// Three dot-separated words, with or without the leading <c>///</c>
    /// (e.g. <c>///offer.loops.carb</c> or <c>offer.loops.carb</c>).
    /// </param>
    /// <param name="apiKey">What3Words API key (from <c>appsettings.local.json</c>).</param>
    /// <returns>
    /// A tuple of (Latitude, Longitude, LocalityName) if resolved successfully,
    /// or <see langword="null"/> if the words cannot be resolved.
    /// </returns>
    /// <sideeffects>
    /// Constructs a <see cref="What3WordsV3"/> wrapper (which manages its own internal
    /// <c>HttpClient</c>) and makes a single <c>convert-to-coordinates</c> request.
    /// Writes warning/error log entries on any failure.
    /// </sideeffects>
    public static async Task<(double Latitude, double Longitude, string LocalityName)?> LookupAsync(
        string words, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Logger.Error("What3WordsClient.LookupAsync called with empty API key — set What3Words:ApiKey in appsettings.local.json.");
            return null;
        }

        var trimmed = words.Trim();
        if (trimmed.StartsWith("///", StringComparison.Ordinal))
            trimmed = trimmed[3..];

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            Logger.Error("What3WordsClient.LookupAsync called with null or empty words — returning null.");
            return null;
        }

        try
        {
            var wrapper = new What3WordsV3(apiKey);
            var result = await wrapper.ConvertToCoordinates(trimmed).RequestAsync();

            if (!result.IsSuccessful)
            {
                var err = result.Error;
                Logger.Warn($"What3Words error for '///{trimmed}': code={err?.Code} message={err?.Message}");
                return null;
            }

            var addr = result.Data;
            if (addr?.Coordinates is null)
            {
                Logger.Warn($"What3Words returned no coordinates for '///{trimmed}'.");
                return null;
            }

            var locality = string.IsNullOrWhiteSpace(addr.NearestPlace) ? "Unknown" : addr.NearestPlace;
            return (addr.Coordinates.Lat, addr.Coordinates.Lng, locality);
        }
        catch (Exception ex)
        {
            Logger.Error($"What3Words lookup failed: {ex.Message}");
            return null;
        }
    }
}
