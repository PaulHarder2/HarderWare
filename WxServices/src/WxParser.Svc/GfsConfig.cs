namespace WxParser.Svc;

/// <summary>
/// Configuration settings for the GFS model-data fetch cycle,
/// bound from the <c>Gfs</c> section of application settings.
/// </summary>
public sealed class GfsConfig
{
    /// <summary>
    /// How many minutes to wait between GFS fetch attempts.
    /// Defaults to 10 minutes; a new GFS run appears every 6 hours and files
    /// are posted incrementally, so checking every 10 minutes allows the fetcher
    /// to resume quickly as new forecast hours become available.
    /// </summary>
    public int IntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Minimum number of hours after the nominal model-run time before the
    /// fetcher will attempt to download data for that run.  GFS output
    /// typically begins appearing on NOMADS/AWS 4–5 hours after initialisation;
    /// this avoids pointless 404 requests during that window.
    /// Defaults to 3.5 hours.
    /// </summary>
    public double DelayHours { get; set; } = 3.5;

    /// <summary>
    /// Highest forecast hour to download (inclusive).
    /// Defaults to 120.
    /// <para>
    /// <b>Important:</b> GFS 0.25° pgrb2 files exist at 1-hour steps from f000
    /// through f120, then at 3-hour steps from f123 onward (f121 and f122 do not
    /// exist).  This class assumes 1-hour steps throughout; do not set this value
    /// above 120 without updating the fetch loop to use 3-hour stepping for the
    /// extended range.
    /// </para>
    /// </summary>
    public int MaxForecastHours { get; set; } = 120;

    /// <summary>
    /// Number of the most-recent GFS model runs to retain in the database.
    /// Older runs are deleted after each successful fetch cycle.
    /// Defaults to 2 (the current run plus the previous one, retained during
    /// the transition period while a new run is being ingested).
    /// </summary>
    public int RetainModelRuns { get; set; } = 2;

    /// <summary>
    /// Absolute path to the <c>wgrib2</c> binary inside the WSL environment,
    /// e.g. <c>/usr/local/bin/wgrib2</c>.
    /// </summary>
    public string Wgrib2WslPath { get; set; } = "/usr/local/bin/wgrib2";

    /// <summary>
    /// Windows directory used for temporary GRIB2, sub-grid, and CSV files
    /// during a fetch cycle.  The directory is created automatically if it does
    /// not exist.  Defaults to <c>C:\HarderWare\temp</c>.
    /// </summary>
    public string TempPath { get; set; } = @"C:\HarderWare\temp";
}
