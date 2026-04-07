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
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, CsvUrl);
            req.Headers.Add("User-Agent", "WxParser/1.0");
            using var resp = await httpClient.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            // OurAirports CSV is UTF-8; read bytes and decode explicitly to
            // avoid any charset mis-detection by the HTTP client.
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            csv = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Logger.Error($"AirportDataImporter: failed to download airports.csv — {ex.Message}");
            return;
        }

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
            if (existing.TryGetValue(icao, out var station))
            {
                station.Name         = data.Name;
                station.Municipality = data.Municipality;
                if (data.Lat.HasValue)         station.Lat         = data.Lat;
                if (data.Lon.HasValue)         station.Lon         = data.Lon;
                if (data.ElevationFt.HasValue) station.ElevationFt = data.ElevationFt;
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
        int idxIcao  = columns.IndexOf("icao_code");
        int idxIdent = columns.IndexOf("ident");
        int idxName  = columns.IndexOf("name");
        int idxMuni  = columns.IndexOf("municipality");
        int idxLat   = columns.IndexOf("latitude_deg");
        int idxLon   = columns.IndexOf("longitude_deg");
        int idxElev  = columns.IndexOf("elevation_ft");

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

            result[icao] = new AirportRow(
                name.Length  > 0 ? Truncate(name, 100)  : null,
                muni.Length  > 0 ? Truncate(muni, 100)  : null,
                lat, lon, elev);
        }

        return result;
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
        double? ElevationFt);
}
