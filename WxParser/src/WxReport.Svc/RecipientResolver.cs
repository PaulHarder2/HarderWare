using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using WxInterp;
using WxParser.Logging;

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
            var geo = await AddressGeocoder.LookupAsync(recipient.Address, _httpClient);
            if (geo is null)
            {
                Logger.Error($"{label}: geocoding failed — skipping.");
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
            recipient.MetarIcao = await WxInterpreter.FindNearestMetarStationAsync(
                recipient.Latitude!.Value, recipient.Longitude!.Value, _dbOptions, _httpClient);

            if (recipient.MetarIcao is null)
            {
                Logger.Error($"{label}: no METAR station found in database — skipping.");
                return false;
            }

            Logger.Info($"{label}: nearest METAR = {recipient.MetarIcao}");
            changed = true;
        }

        if (recipient.TafIcao is null)
        {
            Logger.Info($"{label}: finding nearest TAF station...");
            recipient.TafIcao = await WxInterpreter.FindNearestTafStationAsync(
                recipient.Latitude!.Value, recipient.Longitude!.Value, _dbOptions, _httpClient);

            if (recipient.TafIcao is null)
                Logger.Warn($"{label}: no TAF station found — forecast will be omitted.");
            else
                Logger.Info($"{label}: nearest TAF = {recipient.TafIcao}");

            changed = true;
        }

        if (changed)
            SaveRecipient(recipient);

        return true;
    }

    // ── save-back ─────────────────────────────────────────────────────────────

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
