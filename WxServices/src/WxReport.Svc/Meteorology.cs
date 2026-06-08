namespace WxReport.Svc;

/// <summary>
/// Shared deterministic meteorological helpers used by both the Claude-prompt
/// builder (<see cref="SnapshotDescriber"/>) and the recipient-facing renderer
/// (<see cref="StructuredReportRenderer"/>).  Keeping the physics in one place
/// means the relative-humidity figure Claude reasons over and the figure the
/// recipient sees — and the compass binning in the prompt and the email — can
/// never drift apart.
/// </summary>
internal static class Meteorology
{
    private static readonly string[] CompassPoints =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    /// <summary>Wind direction in degrees true → 16-point compass label (e.g. <c>"SSW"</c>); tolerates negative and &gt;360 inputs.</summary>
    public static string DegreesToCompass(int deg) =>
        CompassPoints[(int)Math.Round(((deg % 360 + 360) % 360) / 22.5) % 16];

    /// <summary>Relative humidity (0–100) from air temperature and dew point (°C) via the August–Roche–Magnus approximation.</summary>
    public static double RelativeHumidity(double tempC, double dewPointC)
    {
        const double a = 17.625;
        const double b = 243.04;
        return 100.0 * Math.Exp(a * dewPointC / (b + dewPointC))
                     / Math.Exp(a * tempC / (b + tempC));
    }
}