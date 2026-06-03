namespace WxServices.Common;

/// <summary>
/// Public-meaningful wind impact bands (WX-110), shared so that everything which
/// asks "is this wind change worth a reader's attention?" agrees on the answer:
/// WxReport's pre-filter material signature (<c>InputIdentity</c>) and the WX-108
/// redundancy backstop (<c>ForecastSnapshotBody.MateriallyEquals</c>).  The
/// thresholds (kt) are stable meteorological constants chosen for what the general
/// public would act on, not aviation precision: ½ tropical-storm force, tropical-
/// storm / gale force, storm force, and hurricane force.
/// </summary>
public static class WindScale
{
    /// <summary>
    /// Maps a wind speed in knots to its impact band:
    /// <c>0</c> = &lt;17 kt (≤ half tropical-storm force, "another Tuesday"),
    /// <c>1</c> = 17–33 (up to tropical-storm / gale force),
    /// <c>2</c> = 34–47 (gale),
    /// <c>3</c> = 48–63 (storm),
    /// <c>4</c> = 64+ (hurricane force).
    /// </summary>
    /// <param name="knots">Wind speed in knots (sustained or gust).</param>
    /// <returns>The impact-band index 0–4.</returns>
    public static int Band(int knots) =>
        knots < 17 ? 0 : knots < 34 ? 1 : knots < 48 ? 2 : knots < 64 ? 3 : 4;
}