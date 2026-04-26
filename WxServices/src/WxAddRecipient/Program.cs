// WxAddRecipient — Address geocoding, METAR station verification, and recipient setup tool.
//
// Usage: WxAddRecipient.exe "<address>"
// Example: WxAddRecipient "34 Stone Springs Circle, The Woodlands, TX 77381"
//
// Workflow:
//   1. Geocodes the supplied address via the Nominatim API (OpenStreetMap).
//   2. Queries the Aviation Weather API for active METAR stations within 2.5°.
//   3. Sorts them by Haversine distance and reports the nearest five.
//   4. For each station, queries the local database for observation count
//      and most-recent observation time.
//   5. Reports whether the address falls within the configured fetch
//      bounding box — if it doesn't, no observations will be collected
//      from that area regardless of station proximity.
//   6. Prompts for recipient configuration fields and appends a new
//      recipient entry to the WxReport.Svc appsettings.local.json.
//      TafIcao is omitted intentionally; RecipientResolver fills it in
//      on the service's first run for the new recipient.
//
// Exit codes:
//   0 — recipient successfully added (or cancelled by user)
//   1 — bad arguments, configuration problem, or write failure
//   2 — address could not be geocoded

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// ── Arguments ─────────────────────────────────────────────────────────────────

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: WxAddRecipient \"<address>\"");
    Console.Error.WriteLine("Example: WxAddRecipient \"34 Stone Springs Circle, The Woodlands, TX 77381\"");
    return 1;
}

var address = args[0].Trim();

// ── Configuration ─────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.shared.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var connStr = config.GetConnectionString("WeatherData");
if (string.IsNullOrWhiteSpace(connStr))
{
    Console.Error.WriteLine("ERROR: ConnectionStrings:WeatherData not found in configuration.");
    return 1;
}

var recipientConfigPath = config["WxAddRecipient:RecipientConfigPath"];
if (string.IsNullOrWhiteSpace(recipientConfigPath))
{
    Console.Error.WriteLine("ERROR: WxAddRecipient:RecipientConfigPath not found in configuration.");
    return 1;
}

var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
    .UseSqlServer(connStr)
    .Options;

double? homeLat = double.TryParse(config["Fetch:HomeLatitude"], out var hl) ? hl : null;
double? homeLon = double.TryParse(config["Fetch:HomeLongitude"], out var hlo) ? hlo : null;
double? boxDeg = double.TryParse(config["Fetch:BoundingBoxDegrees"], out var bd) ? bd : null;

var defaultLanguage = config["Report:DefaultLanguage"] ?? "English";

// ── Geocode address ───────────────────────────────────────────────────────────

Console.WriteLine($"Address  : {address}");
Console.WriteLine();

using var http = new HttpClient();
var geo = await AddressGeocoder.LookupAsync(address, http);

if (geo is null)
{
    Console.Error.WriteLine("ERROR: Address could not be geocoded. Check spelling and try again.");
    return 2;
}

var (lat, lon, locality) = geo.Value;
Console.WriteLine($"Resolved : {locality}  ({lat:F4}°N, {Math.Abs(lon):F4}°{(lon < 0 ? "W" : "E")})");
Console.WriteLine();

// ── Find nearby active METAR stations ────────────────────────────────────────
// The Aviation Weather API returns only stations with a recent observation,
// so any result here is actively reporting.  We use a ±2.5° search box which
// covers roughly 275 km in each direction at this latitude.

const double SearchRadius = 2.5;
var bbox = $"{lat - SearchRadius},{lon - SearchRadius},{lat + SearchRadius},{lon + SearchRadius}";
var url = $"https://aviationweather.gov/api/data/metar?bbox={bbox}&hours=1&format=json";

AwcMetar[]? awcResults;
try
{
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Add("User-Agent", "WxAddRecipient/1.0");
    using var resp = await http.SendAsync(req);
    resp.EnsureSuccessStatusCode();
    awcResults = await resp.Content.ReadFromJsonAsync<AwcMetar[]>();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Aviation Weather API request failed: {ex.Message}");
    return 1;
}

if (awcResults is not { Length: > 0 })
{
    Console.Error.WriteLine($"WARNING: No active METAR stations found within {SearchRadius}° of the resolved coordinates.");
    awcResults = [];
}

// Sort by Haversine great-circle distance and take the five nearest.
static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
{
    const double R = 6371.0;
    var dLat = (lat2 - lat1) * Math.PI / 180.0;
    var dLon = (lon2 - lon1) * Math.PI / 180.0;
    var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                      + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                      * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
}

var candidates = awcResults
    .Where(s => s.IcaoId is not null && s.Lat.HasValue && s.Lon.HasValue)
    .DistinctBy(s => s.IcaoId)
    .Select(s => (Station: s, DistKm: HaversineKm(lat, lon, s.Lat!.Value, s.Lon!.Value)))
    .OrderBy(x => x.DistKm)
    .Take(5)
    .ToList();

// ── Query database for each station ──────────────────────────────────────────

Console.WriteLine($"  {"#",2}  {"ICAO",-5}  {"Distance",8}  {"METARs",8}  {"TAFs",8}  Latest observation");
Console.WriteLine($"  {new string('-', 72)}");

await using var ctx = new WeatherDataContext(dbOptions);
int nearestWithObs = -1;

for (int i = 0; i < candidates.Count; i++)
{
    var (stn, distKm) = candidates[i];
    var icao = stn.IcaoId!;

    var metarCount = await ctx.Metars.CountAsync(m => m.StationIcao == icao);
    var tafCount = await ctx.Tafs.CountAsync(t => t.StationIcao == icao);

    DateTime? latest = metarCount > 0
        ? await ctx.Metars
            .Where(m => m.StationIcao == icao)
            .MaxAsync(m => m.ObservationUtc)
        : null;

    var name = await ctx.WxStations
        .Where(s => s.IcaoId == icao)
        .Select(s => s.Name)
        .FirstOrDefaultAsync();

    var metarCol = metarCount == 0 ? "—" : $"{metarCount}";
    var tafCol = tafCount == 0 ? "—" : $"{tafCount}";

    string latestCol;
    if (latest.HasValue)
    {
        var age = DateTime.UtcNow - latest.Value;
        latestCol = $"{latest.Value:yyyy-MM-dd HH:mm}Z  ({age.TotalHours:F1} h ago)";
    }
    else
    {
        latestCol = "not in database";
    }

    var nameNote = name is not null ? $"  [{name}]" : "";
    Console.WriteLine($"  {i + 1,2}  {icao,-5}  {distKm,6:F1} km  {metarCol,8}  {tafCol,8}  {latestCol}{nameNote}");

    if (nearestWithObs < 0 && metarCount > 0)
        nearestWithObs = i;
}

Console.WriteLine();

// ── Fetch bounding box check ──────────────────────────────────────────────────

if (homeLat.HasValue && homeLon.HasValue && boxDeg.HasValue)
{
    var latMin = homeLat.Value - boxDeg.Value;
    var latMax = homeLat.Value + boxDeg.Value;
    var lonMin = homeLon.Value - boxDeg.Value;
    var lonMax = homeLon.Value + boxDeg.Value;
    var inBox = lat >= latMin && lat <= latMax && lon >= lonMin && lon <= lonMax;

    Console.WriteLine($"Fetch bbox : {latMin:F1}°–{latMax:F1}°N, {Math.Abs(lonMax):F1}°–{Math.Abs(lonMin):F1}°W  (home ±{boxDeg:F0}°)");
    var bboxStatus = inBox
        ? "Address is WITHIN the fetch bounding box."
        : "Address is OUTSIDE the fetch bounding box — nearby stations will not have observations.";
    Console.WriteLine($"             {bboxStatus}");
    Console.WriteLine();
}

// ── Station summary ───────────────────────────────────────────────────────────

var suggestedMetar = "";
if (nearestWithObs >= 0)
{
    var best = candidates[nearestWithObs];
    suggestedMetar = best.Station.IcaoId!;
    if (nearestWithObs == 0)
        Console.WriteLine($"OK: Nearest station {suggestedMetar} ({best.DistKm:F1} km) has database observations.");
    else
        Console.WriteLine(
            $"NOTE: Nearest station {candidates[0].Station.IcaoId} has no observations; " +
            $"suggested METAR station: {suggestedMetar} ({best.DistKm:F1} km, rank {nearestWithObs + 1}).");
}
else if (candidates.Count > 0)
{
    suggestedMetar = candidates[0].Station.IcaoId!;
    Console.WriteLine("WARNING: None of the nearest stations have observations in the database.");
}
else
{
    Console.WriteLine("WARNING: No nearby METAR stations found.");
}

Console.WriteLine();

// ── Recipient prompts ─────────────────────────────────────────────────────────

static string Prompt(string label, string? defaultValue = null)
{
    var hint = defaultValue is not null ? $" [{defaultValue}]" : "";
    while (true)
    {
        Console.Write($"  {label}{hint}: ");
        var input = Console.ReadLine()?.Trim() ?? "";
        if (input.Length > 0) return input;
        if (defaultValue is not null) return defaultValue;
        Console.WriteLine("    (required — please enter a value)");
    }
}

Console.WriteLine("─── New recipient ────────────────────────────────────────────────────────────");
Console.WriteLine();

var recipientId = Prompt("Id (e.g. \"john-en\")");
var recipientName = Prompt("Name");
var email = Prompt("Email");
var language = Prompt("Language", defaultValue: defaultLanguage);
var timezone = Prompt("Timezone", defaultValue: "America/Chicago");
var schedHours = Prompt("Scheduled send hour(s) (e.g. \"7\" or \"6, 18\")", defaultValue: "7");
var metarIcao = Prompt("METAR ICAO(s)", defaultValue: suggestedMetar.Length > 0 ? suggestedMetar : null);
var tempUnit = Prompt("Temperature unit (F/C)", defaultValue: "F");
var pressureUnit = Prompt("Pressure unit (inHg/kPa)", defaultValue: "inHg");
var windUnit = Prompt("Wind speed unit (mph/kph)", defaultValue: "mph");

Console.WriteLine();

// ── Confirm ───────────────────────────────────────────────────────────────────

Console.WriteLine("─── Will write ───────────────────────────────────────────────────────────────");
Console.WriteLine();
Console.WriteLine($"  Id               : {recipientId}");
Console.WriteLine($"  Name             : {recipientName}");
Console.WriteLine($"  Email            : {email}");
Console.WriteLine($"  Language         : {language}");
Console.WriteLine($"  Timezone         : {timezone}");
Console.WriteLine($"  Scheduled hours  : {schedHours}");
Console.WriteLine($"  Address          : {address}");
Console.WriteLine($"  Locality         : {locality}");
Console.WriteLine($"  Latitude         : {lat:F7}");
Console.WriteLine($"  Longitude        : {lon:F7}");
Console.WriteLine($"  MetarIcao        : {metarIcao}");
Console.WriteLine($"  TafIcao          : (resolved by service on first run)");
Console.WriteLine($"  Units            : temp={tempUnit}  pressure={pressureUnit}  wind={windUnit}");
Console.WriteLine();
Console.WriteLine($"  Target: {recipientConfigPath}");
Console.WriteLine();

Console.Write("Confirm? (y/N): ");
var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
if (confirm != "y" && confirm != "yes")
{
    Console.WriteLine("Cancelled.");
    return 0;
}

// ── Write to config ───────────────────────────────────────────────────────────

try
{
    JsonNode root = File.Exists(recipientConfigPath)
        ? JsonNode.Parse(File.ReadAllText(recipientConfigPath)) ?? new JsonObject()
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

    foreach (var item in recipientsArray)
    {
        if (item?["Id"]?.GetValue<string>() == recipientId)
        {
            Console.Error.WriteLine($"ERROR: A recipient with Id \"{recipientId}\" already exists in the target file.");
            return 1;
        }
    }

    recipientsArray.Add(new JsonObject
    {
        ["Id"] = recipientId,
        ["Email"] = email,
        ["Name"] = recipientName,
        ["Address"] = address,
        ["LocalityName"] = locality,
        ["Language"] = language,
        ["Timezone"] = timezone,
        ["ScheduledSendHours"] = schedHours,
        ["Latitude"] = lat,
        ["Longitude"] = lon,
        ["MetarIcao"] = metarIcao,
        ["Units"] = new JsonObject
        {
            ["Temperature"] = tempUnit,
            ["Pressure"] = pressureUnit,
            ["WindSpeed"] = windUnit,
        },
    });

    File.WriteAllText(recipientConfigPath,
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine();
    Console.WriteLine($"OK: Recipient \"{recipientId}\" added to {Path.GetFileName(recipientConfigPath)}");
    Console.WriteLine("WxReport.Svc will pick up the new recipient automatically on its next cycle.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Failed to write configuration: {ex.Message}");
    return 1;
}

return 0;

// ── Aviation Weather API DTOs ─────────────────────────────────────────────────

internal sealed class AwcMetar
{
    [JsonPropertyName("icaoId")] public string? IcaoId { get; set; }
    [JsonPropertyName("lat")] public double? Lat { get; set; }
    [JsonPropertyName("lon")] public double? Lon { get; set; }
}