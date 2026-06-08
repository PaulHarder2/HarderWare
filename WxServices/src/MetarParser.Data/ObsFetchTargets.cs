using Microsoft.EntityFrameworkCore;

namespace MetarParser.Data;

/// <summary>
/// The fetch-coverage inputs derived from persisted localities and recipients
/// (WX-140): the points whose neighborhoods observation fetching must cover,
/// and the station ICAOs they explicitly name.  Loaded in one place so the
/// per-cycle fetch worker and WxManager's recipient-save flow (WX-141) cannot
/// drift apart on the subtle extraction rules (comma lists, the
/// case-insensitive <c>NONE</c> sentinel).
/// </summary>
/// <param name="Points">Locality centroids plus locality-less recipients' coordinates.</param>
/// <param name="MetarIcaos">Distinct METAR stations named by any locality or recipient.</param>
/// <param name="TafIcaos">Distinct TAF stations named by any locality or recipient.</param>
public sealed record ObsFetchTargets(
    IReadOnlyList<(double Lat, double Lon)> Points,
    IReadOnlyList<string> MetarIcaos,
    IReadOnlyList<string> TafIcaos)
{
    /// <summary>Loads the current targets from the database.</summary>
    /// <param name="dbOptions">EF Core options.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ObsFetchTargets> LoadAsync(
        DbContextOptions<WeatherDataContext> dbOptions, CancellationToken ct = default)
    {
        await using var db = new WeatherDataContext(dbOptions);

        var localityPoints = await db.Localities
            .Where(l => l.CentroidLat != null && l.CentroidLon != null)
            .Select(l => new { Lat = l.CentroidLat!.Value, Lon = l.CentroidLon!.Value })
            .ToListAsync(ct);
        var recipientPoints = await db.Recipients
            .Where(r => r.LocalityId == null && r.Latitude != null && r.Longitude != null)
            .Select(r => new { Lat = r.Latitude!.Value, Lon = r.Longitude!.Value })
            .ToListAsync(ct);
        var points = localityPoints.Concat(recipientPoints).Select(p => (p.Lat, p.Lon)).ToList();

        var metarLists = (await db.Localities.Select(l => l.MetarIcao).ToListAsync(ct))
            .Concat(await db.Recipients.Select(r => r.MetarIcao).ToListAsync(ct));
        var tafLists = (await db.Localities.Select(l => l.TafIcao).ToListAsync(ct))
            .Concat(await db.Recipients.Select(r => r.TafIcao).ToListAsync(ct));

        return new ObsFetchTargets(points, IcaoListFormat.Parse(metarLists), IcaoListFormat.Parse(tafLists));
    }
}

/// <summary>
/// The comma-separated ICAO-list wire format shared by <c>MetarIcao</c> /
/// <c>TafIcao</c> columns (WX-140; the ScheduledSendHoursFormat precedent from
/// WX-127 — one parser for one format, not three inline copies).  Entries are
/// trimmed; blanks and the <c>NONE</c> sentinel (any casing, per entry) are
/// dropped; duplicates collapse case-insensitively.
/// </summary>
public static class IcaoListFormat
{
    /// <summary>Parses one or more comma-separated ICAO list values into distinct station identifiers.</summary>
    /// <param name="values">Raw column values; nulls and blanks are tolerated.</param>
    public static IReadOnlyList<string> Parse(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .SelectMany(v => v!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Where(icao => !string.Equals(icao, "NONE", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}