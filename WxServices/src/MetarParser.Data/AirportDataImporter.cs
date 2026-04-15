using System.Globalization;
using System.Text;
using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Downloads the OurAirports airports.csv dataset and upserts the full set
/// of ICAO airports into the <c>WxStations</c> table.
/// <para>
/// Existing rows are updated with properly-cased names, municipality, and
/// coordinates.  Airports not yet in the table are inserted so that any
/// station that later reports a METAR already has its metadata populated.
/// </para>
/// </summary>
public static class AirportDataImporter
{
    private const string CsvUrl =
        "https://davidmegginson.github.io/ourairports-data/airports.csv";

    private const string CountriesCsvUrl =
        "https://davidmegginson.github.io/ourairports-data/countries.csv";

    private const string RegionsCsvUrl =
        "https://davidmegginson.github.io/ourairports-data/regions.csv";

    /// <summary>
    /// Display-friendly country abbreviations that differ from the ISO 3166-1
    /// alpha-2 code.  Stations whose <c>iso_country</c> is not in this table
    /// receive <see cref="WxStation.CountryAbbr"/> equal to their
    /// <see cref="WxStation.CountryCode"/>.  Extend as we encounter reports
    /// where the raw ISO code reads awkwardly.
    /// </summary>
    private static readonly Dictionary<string, string> CountryAbbrOverrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["GB"] = "UK",
            ["US"] = "USA",
        };

    /// <summary>
    /// Downloads airports.csv from OurAirports and updates all matching rows
    /// in the <c>WxStations</c> table.
    /// </summary>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/>.</param>
    /// <param name="httpClient">HTTP client used to download the CSV.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RefreshAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient,
        CancellationToken ct = default)
    {
        string csv;
        string countriesCsv;
        string regionsCsv;
        try
        {
            csv          = await DownloadAsync(httpClient, CsvUrl,          ct);
            countriesCsv = await DownloadAsync(httpClient, CountriesCsvUrl, ct);
            regionsCsv   = await DownloadAsync(httpClient, RegionsCsvUrl,   ct);
        }
        catch (Exception ex)
        {
            Logger.Error($"AirportDataImporter: failed to download OurAirports CSV — {ex.Message}");
            return;
        }

        var countryNames = ParseCountries(countriesCsv);
        var regionNames  = ParseRegions(regionsCsv);
        Logger.Info($"AirportDataImporter: loaded {countryNames.Count} countries, {regionNames.Count} regions.");

        var airportData = ParseCsv(csv);
        Logger.Info($"AirportDataImporter: parsed {airportData.Count} airports from OurAirports.");

        await using var ctx = new WeatherDataContext(dbOptions);

        // Load existing stations into a dictionary for fast lookup.
        var existing = await ctx.WxStations
            .ToDictionaryAsync(s => s.IcaoId.Trim(), StringComparer.OrdinalIgnoreCase, ct);

        int updated  = 0;
        int inserted = 0;

        foreach (var (icao, data) in airportData)
        {
            var countryCode = string.IsNullOrWhiteSpace(data.CountryCode) ? null : data.CountryCode;
            var regionCode  = string.IsNullOrWhiteSpace(data.RegionCode)  ? null : data.RegionCode;

            // OurAirports emits a placeholder subdivision "{country}-U-A" with name
            // "(unassigned)" for airports whose country lacks or omits a subdivision.
            // Treat these as absent so the station-grid display doesn't read
            // "Vega Baja, U-A, PR" — we leave the country intact and drop the region.
            if (regionCode is not null && regionCode.EndsWith("-U-A", StringComparison.Ordinal))
                regionCode = null;

            string? regionAbbr = null;
            if (regionCode is not null)
            {
                int dash = regionCode.IndexOf('-');
                regionAbbr = dash >= 0 && dash + 1 < regionCode.Length
                    ? regionCode[(dash + 1)..]
                    : regionCode;
            }

            string? country = countryCode is not null && countryNames.TryGetValue(countryCode, out var cn) ? cn : null;
            string? region  = regionCode  is not null && regionNames.TryGetValue(regionCode,   out var rn) ? rn : null;

            string? countryAbbr = countryCode is null
                ? null
                : (CountryAbbrOverrides.TryGetValue(countryCode, out var ab) ? ab : countryCode);

            if (existing.TryGetValue(icao, out var station))
            {
                station.Name         = data.Name;
                station.Municipality = data.Municipality;
                if (data.Lat.HasValue)         station.Lat         = data.Lat;
                if (data.Lon.HasValue)         station.Lon         = data.Lon;
                if (data.ElevationFt.HasValue) station.ElevationFt = data.ElevationFt;
                station.Region      = region;
                station.RegionCode  = regionCode;
                station.RegionAbbr  = regionAbbr;
                station.Country     = country;
                station.CountryCode = countryCode;
                station.CountryAbbr = countryAbbr;
                updated++;
            }
            else
            {
                ctx.WxStations.Add(new WxStation
                {
                    IcaoId       = icao,
                    Name         = data.Name,
                    Municipality = data.Municipality,
                    Lat          = data.Lat,
                    Lon          = data.Lon,
                    ElevationFt  = data.ElevationFt,
                    Region       = region,
                    RegionCode   = regionCode,
                    RegionAbbr   = regionAbbr,
                    Country      = country,
                    CountryCode  = countryCode,
                    CountryAbbr  = countryAbbr,
                });
                inserted++;
            }
        }

        await ctx.SaveChangesAsync(ct);
        Logger.Info($"AirportDataImporter: updated {updated}, inserted {inserted} station(s).");
    }

    // ── CSV parsing ───────────────────────────────────────────────────────────

    private static Dictionary<string, AirportRow> ParseCsv(string csv)
    {
        var result = new Dictionary<string, AirportRow>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine();
        if (headerLine is null) return result;

        var columns  = SplitCsvLine(headerLine);
        int idxIcao    = columns.IndexOf("icao_code");
        int idxIdent   = columns.IndexOf("ident");
        int idxName    = columns.IndexOf("name");
        int idxMuni    = columns.IndexOf("municipality");
        int idxLat     = columns.IndexOf("latitude_deg");
        int idxLon     = columns.IndexOf("longitude_deg");
        int idxElev    = columns.IndexOf("elevation_ft");
        int idxCountry = columns.IndexOf("iso_country");
        int idxRegion  = columns.IndexOf("iso_region");

        if (idxName < 0 || idxMuni < 0) return result;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsvLine(line);
            int maxIdx = Math.Max(idxName, idxMuni);
            if (idxIcao  >= 0) maxIdx = Math.Max(maxIdx, idxIcao);
            if (idxIdent >= 0) maxIdx = Math.Max(maxIdx, idxIdent);
            if (fields.Count <= maxIdx) continue;

            // Prefer icao_code; fall back to ident for airports (e.g. K11R) that
            // carry only an FAA identifier and leave icao_code blank.
            var icao = idxIcao  >= 0 ? fields[idxIcao].Trim()  : "";
            if (string.IsNullOrEmpty(icao))
                icao  = idxIdent >= 0 ? fields[idxIdent].Trim() : "";
            if (icao.Length != 4) continue;  // IcaoId column is nchar(4); skip non-standard identifiers

            var name = fields[idxName].Trim();
            var muni = fields[idxMuni].Trim();

            double? lat  = TryParseDouble(fields, idxLat);
            double? lon  = TryParseDouble(fields, idxLon);
            double? elev = TryParseDouble(fields, idxElev);

            var country = idxCountry >= 0 && idxCountry < fields.Count ? fields[idxCountry].Trim() : "";
            var region  = idxRegion  >= 0 && idxRegion  < fields.Count ? fields[idxRegion].Trim()  : "";

            result[icao] = new AirportRow(
                name.Length    > 0 ? Truncate(name, 100)  : null,
                muni.Length    > 0 ? Truncate(muni, 100)  : null,
                lat, lon, elev,
                country.Length > 0 ? Truncate(country, 2) : null,
                region.Length  > 0 ? Truncate(region, 10) : null);
        }

        return result;
    }

    /// <summary>
    /// Parses OurAirports <c>countries.csv</c> into a map of ISO 3166-1 alpha-2
    /// code → country short name (e.g. <c>"US"</c> → <c>"United States"</c>).
    /// </summary>
    private static Dictionary<string, string> ParseCountries(string csv)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine();
        if (headerLine is null) return result;

        var columns = SplitCsvLine(headerLine);
        int idxCode = columns.IndexOf("code");
        int idxName = columns.IndexOf("name");
        if (idxCode < 0 || idxName < 0) return result;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsvLine(line);
            if (fields.Count <= Math.Max(idxCode, idxName)) continue;

            var code = fields[idxCode].Trim();
            var name = fields[idxName].Trim();
            if (code.Length == 0 || name.Length == 0) continue;

            result[code] = Truncate(name, 100);
        }

        return result;
    }

    /// <summary>
    /// Parses OurAirports <c>regions.csv</c> into a map of full ISO 3166-2
    /// subdivision code → region name (e.g. <c>"US-TX"</c> → <c>"Texas"</c>).
    /// </summary>
    private static Dictionary<string, string> ParseRegions(string csv)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(csv);
        var headerLine = reader.ReadLine();
        if (headerLine is null) return result;

        var columns = SplitCsvLine(headerLine);
        int idxCode = columns.IndexOf("code");
        int idxName = columns.IndexOf("name");
        if (idxCode < 0 || idxName < 0) return result;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsvLine(line);
            if (fields.Count <= Math.Max(idxCode, idxName)) continue;

            var code = fields[idxCode].Trim();
            var name = fields[idxName].Trim();
            if (code.Length == 0 || name.Length == 0) continue;

            result[code] = Truncate(name, 100);
        }

        return result;
    }

    private static async Task<string> DownloadAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("User-Agent", "WxParser/1.0");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        // OurAirports CSV is UTF-8; decode explicitly to avoid charset mis-detection.
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private static double? TryParseDouble(List<string> fields, int idx)
    {
        if (idx < 0 || idx >= fields.Count) return null;
        return double.TryParse(fields[idx], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen];

    /// <summary>
    /// Splits one CSV line into fields, handling double-quoted fields and
    /// escaped double-quotes (<c>""</c>).
    /// </summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields   = new List<string>();
        var sb       = new StringBuilder();
        bool inQuote = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuote && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuote = !inQuote;
                }
            }
            else if (c == ',' && !inQuote)
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private record AirportRow(
        string? Name,
        string? Municipality,
        double? Lat,
        double? Lon,
        double? ElevationFt,
        string? CountryCode,
        string? RegionCode);
}
