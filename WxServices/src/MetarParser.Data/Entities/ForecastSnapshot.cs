namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one committed forecast snapshot for a single station.
/// A snapshot is the structured, machine-readable representation of what
/// WxReport told the recipient at the moment of a commit: uniform 6-hour
/// blocks covering up to a six-day horizon, serialized as JSON in
/// <see cref="Body"/> per the <see cref="ForecastSnapshotBody"/> schema.
///
/// Introduced under WX-76 as the foundation for the WX-47 rearchitecture.
/// Later cycles diff against the most recent snapshot for a station to
/// decide whether the forecast has been invalidated — replacing the
/// observation-to-observation fingerprint logic that produced the
/// 2026-04-21 KDWH double-send.
/// </summary>
public sealed class ForecastSnapshot
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Four-letter ICAO of the station the snapshot is for, e.g. <c>"KDWH"</c>.
    /// Not a database foreign key: matches the convention used by
    /// <see cref="MetarRecord.StationIcao"/> and <see cref="TafRecord.StationIcao"/>.
    /// </summary>
    public string StationIcao { get; set; } = "";

    /// <summary>
    /// UTC time at which this snapshot was committed (written or overwritten
    /// by the report cycle).  The unique index on
    /// (<see cref="StationIcao"/>, <see cref="GeneratedAtUtc"/>) prevents
    /// duplicate snapshots at the same instant for the same station and
    /// supports the most-recent-snapshot lookup.
    /// </summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>
    /// Schema version of the JSON in <see cref="Body"/>.  Persisted both in
    /// this column (cheap to scan without parsing) and inside the body itself
    /// (authoritative for stand-alone diagnostics).  Bumps if the body shape
    /// changes incompatibly.  Defaults to
    /// <see cref="ForecastSnapshotBody.SchemaVersionCurrent"/>.
    /// </summary>
    public int SchemaVersion { get; set; } = ForecastSnapshotBody.SchemaVersionCurrent;

    /// <summary>
    /// JSON-serialized <see cref="ForecastSnapshotBody"/>.  Stored as
    /// <c>nvarchar(max)</c> to accommodate a full six-day horizon (24 blocks).
    /// Round-trip via <see cref="ForecastSnapshotBody.Serialize"/> and
    /// <see cref="ForecastSnapshotBody.Deserialize"/>.
    /// </summary>
    public string Body { get; set; } = "";
}