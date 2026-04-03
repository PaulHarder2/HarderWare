// WxIdentify — Address geocoding and METAR station verification tool.
//
// Usage: WxIdentify.exe "<address>"
// Example: WxIdentify "306 Scenic Brook Street, Brenham, TX 77833"
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
//
// Exit codes:
//   0 — address resolved; nearest station has database observations
//   1 — bad arguments or configuration problem
//   2 — address could not be geocoded
//   3 — address geocoded but no nearby station has database observations

using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

// ── Arguments ─────────────────────────────────────────────────────────────────

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: WxIdentify \"<address>\"");
    Console.Error.WriteLine("Example: WxIdentify \"306 Scenic Brook Street, Brenham, TX 77833\"");
    return 1;
}

var address = args[0].Trim();

// ── Configuration ─────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.shared.json", optional: false)
    .AddJsonFile("appsettings.local.json",  optional: true)
    .Build();

var connStr = config.GetConnectionString("WeatherData");
if (string.IsNullOrWhiteSpace(connStr))
{
    Console.Error.WriteLine("ERROR: ConnectionStrings:WeatherData not found in configuration.");
    return 1;
}

var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
    .UseSqlServer(connStr)
    .Options;

double? homeLat = double.TryParse(config["Fetch:HomeLatitude"],       out var hl)  ? hl  : null;
double? homeLon = double.TryParse(config["Fetch:HomeLongitude"],      out var hlo) ? hlo : null;
double? boxDeg  = double.TryParse(config["Fetch:BoundingBoxDegrees"], out var bd)  ? bd  : null;

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
var url  = $"https://aviationweather.gov/api/data/metar?bbox={bbox}&hours=1&format=json";

AwcMetar[]? awcResults;
try
{
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Add("User-Agent", "WxIdentify/1.0");
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
    Console.Error.WriteLine($"No active METAR stations found within {SearchRadius}° of the resolved coordinates.");
    return 3;
}

// Sort by Haversine great-circle distance and take the five nearest.
static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
{
    const double R    = 6371.0;
    var          dLat = (lat2 - lat1) * Math.PI / 180.0;
    var          dLon = (lon2 - lon1) * Math.PI / 180.0;
    var          a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
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

Console.WriteLine($"  {"#",2}  {"ICAO",-5}  {"Distance",8}  {"In DB",10}  Latest observation");
Console.WriteLine($"  {new string('-', 68)}");

await using var ctx = new WeatherDataContext(dbOptions);
int nearestWithObs = -1;

for (int i = 0; i < candidates.Count; i++)
{
    var (stn, distKm) = candidates[i];
    var icao = stn.IcaoId!;

    var count = await ctx.Metars.CountAsync(m => m.StationIcao == icao);

    DateTime? latest = count > 0
        ? await ctx.Metars
            .Where(m => m.StationIcao == icao)
            .MaxAsync(m => m.ObservationUtc)
        : null;

    var name = await ctx.WxStations
        .Where(s => s.IcaoId == icao)
        .Select(s => s.Name)
        .FirstOrDefaultAsync();

    var obsCol = count == 0 ? "—" : $"{count} obs";

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
    Console.WriteLine($"  {i + 1,2}  {icao,-5}  {distKm,6:F1} km  {obsCol,10}  {latestCol}{nameNote}");

    if (nearestWithObs < 0 && count > 0)
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
    var inBox  = lat >= latMin && lat <= latMax && lon >= lonMin && lon <= lonMax;

    Console.WriteLine($"Fetch bbox : {latMin:F1}°–{latMax:F1}°N, {Math.Abs(lonMax):F1}°–{Math.Abs(lonMin):F1}°W  (home ±{boxDeg:F0}°)");
    var bboxStatus = inBox
        ? "Address is WITHIN the fetch bounding box."
        : "Address is OUTSIDE the fetch bounding box — nearby stations will not have observations.";
    Console.WriteLine($"             {bboxStatus}");
    Console.WriteLine();
}

// ── Summary ───────────────────────────────────────────────────────────────────

if (nearestWithObs < 0)
{
    Console.Error.WriteLine("WARNING: None of the nearest stations have observations in the database.");
    return 3;
}

var best = candidates[nearestWithObs];
if (nearestWithObs == 0)
{
    Console.WriteLine($"OK: Nearest station {best.Station.IcaoId} ({best.DistKm:F1} km) has database observations.");
}
else
{
    Console.WriteLine(
        $"NOTE: Nearest station {candidates[0].Station.IcaoId} has no observations; " +
        $"nearest with observations is {best.Station.IcaoId} ({best.DistKm:F1} km, rank {nearestWithObs + 1}).");
}

return 0;

// ── Aviation Weather API DTOs ─────────────────────────────────────────────────

internal sealed class AwcMetar
{
    [JsonPropertyName("icaoId")] public string? IcaoId { get; set; }
    [JsonPropertyName("lat")]    public double? Lat    { get; set; }
    [JsonPropertyName("lon")]    public double? Lon    { get; set; }
}
