namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>GfsModelRuns</c> table.
/// Tracks whether a GFS model run has been fully ingested into the
/// <c>GfsGrid</c> table and is therefore safe for downstream consumers
/// (e.g. WxInterp) to read from.
/// </summary>
/// <remarks>
/// A row is inserted with <see cref="IsComplete"/> = <see langword="false"/> when
/// <c>GfsFetcher</c> begins downloading a new run.  It is updated to
/// <see langword="true"/> only after every configured forecast hour has been
/// successfully inserted into <c>GfsGrid</c>.
/// <para>
/// While two runs are simultaneously present (one complete, one still being
/// downloaded), consumers should always select the most recent run whose
/// <see cref="IsComplete"/> flag is <see langword="true"/>.
/// </para>
/// </remarks>
public sealed class GfsModelRun
{
    /// <summary>
    /// UTC date and time at which the GFS model run was initialised.
    /// This is the primary key.  GFS runs at 00Z, 06Z, 12Z, and 18Z each day.
    /// </summary>
    public DateTime ModelRunUtc { get; set; }

    /// <summary>
    /// <see langword="true"/> when all configured forecast hours for this run
    /// have been inserted into the <c>GfsGrid</c> table and the run is ready
    /// for use.  <see langword="false"/> while ingestion is in progress.
    /// </summary>
    public bool IsComplete { get; set; }
}