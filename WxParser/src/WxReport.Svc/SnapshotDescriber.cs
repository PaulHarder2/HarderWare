using System.Text;
using WxInterp;

namespace WxReport.Svc;

/// <summary>
/// Converts a <see cref="WeatherSnapshot"/> into a structured plain-text
/// description that can be supplied to the Claude API as a data payload.
/// </summary>
public static class SnapshotDescriber
{
    public static string Describe(WeatherSnapshot snap, TimeZoneInfo tz)
    {
        var sb = new StringBuilder();

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var localObs = TimeZoneInfo.ConvertTimeFromUtc(snap.ObservationTimeUtc, tz);

        sb.AppendLine($"Current date/time: {localNow:dddd, yyyy-MM-dd HH:mm} local / {DateTime.UtcNow:HH:mm} UTC");
        sb.AppendLine($"Station: {snap.StationIcao} ({snap.LocalityName})");
        sb.AppendLine($"Observed: {localObs:dddd, yyyy-MM-dd HH:mm} local / {snap.ObservationTimeUtc:HH:mm} UTC{(snap.IsAutomated ? " (automated)" : "")}");

        // Wind
        if (snap.WindIsVariable)
            sb.AppendLine($"Wind: variable at {snap.WindSpeedKt?.ToString() ?? "calm"} kt ({KtToMph(snap.WindSpeedKt)} mph){GustStr(snap.WindGustKt)}");
        else if (snap.WindSpeedKt is 0 or null)
            sb.AppendLine("Wind: calm");
        else
            sb.AppendLine($"Wind: {snap.WindDirectionDeg:000}° at {snap.WindSpeedKt} kt ({KtToMph(snap.WindSpeedKt)} mph){GustStr(snap.WindGustKt)}");

        // Visibility
        if (snap.Cavok)
            sb.AppendLine("Visibility: CAVOK");
        else if (snap.VisibilityStatuteMiles.HasValue)
            sb.AppendLine($"Visibility: {(snap.VisibilityLessThan ? "<" : "")}{snap.VisibilityStatuteMiles.Value:0.##} SM");
        else
            sb.AppendLine("Visibility: not reported");

        // Sky
        if (snap.SkyLayers.Count == 0)
            sb.AppendLine("Sky: clear");
        else
            foreach (var layer in snap.SkyLayers)
                sb.AppendLine($"Sky: {FormatCoverage(layer.Coverage)}{(layer.HeightFeet.HasValue ? $" at {layer.HeightFeet} ft" : "")}{(layer.CloudType != CloudType.None ? $" ({layer.CloudType})" : "")}");

        // Weather phenomena
        foreach (var w in snap.WeatherPhenomena)
            sb.AppendLine($"Weather: {FormatPhenomenon(w)}");

        // Temperature / dew point
        if (snap.TemperatureFahrenheit.HasValue)
            sb.AppendLine($"Temperature: {snap.TemperatureFahrenheit.Value:0}°F ({snap.TemperatureCelsius?.ToString("0") ?? "?"}°C)");
        if (snap.DewPointCelsius.HasValue)
            sb.AppendLine($"Dew point: {snap.DewPointCelsius.Value:0}°C");

        // Altimeter
        if (snap.AltimeterInHg.HasValue)
            sb.AppendLine($"Altimeter: {snap.AltimeterInHg.Value:0.00} inHg");

        // Forecast
        if (!string.IsNullOrEmpty(snap.TafStationIcao))
        {
            sb.AppendLine($"Forecast source: {snap.TafStationIcao}");
            foreach (var p in snap.ForecastPeriods)
                sb.AppendLine(FormatForecastPeriod(p));
        }
        else
        {
            sb.AppendLine("Forecast: not available");
        }

        return sb.ToString();
    }

    // ── formatting helpers ────────────────────────────────────────────────────

    private static string GustStr(int? gust) =>
        gust.HasValue ? $", gusting {gust} kt ({KtToMph(gust)} mph)" : "";

    private static int KtToMph(int? kt) =>
        kt.HasValue ? (int)Math.Round(kt.Value * 1.15078) : 0;

    private static string FormatCoverage(SkyCoverage c) => c switch
    {
        SkyCoverage.Clear               => "Clear",
        SkyCoverage.Few                 => "Few",
        SkyCoverage.Scattered           => "Scattered",
        SkyCoverage.Broken              => "Broken",
        SkyCoverage.Overcast            => "Overcast",
        SkyCoverage.VerticalVisibility  => "Vertical visibility",
        SkyCoverage.NoSignificantCloud  => "No significant cloud",
        SkyCoverage.NoCloudsDetected    => "No clouds detected",
        _                               => c.ToString(),
    };

    private static string FormatPhenomenon(SnapshotWeather w)
    {
        var parts = new List<string>();

        if (w.Intensity != WeatherIntensity.Moderate)
            parts.Add(w.Intensity.ToString());
        if (w.Descriptor.HasValue)
            parts.Add(w.Descriptor.Value.ToString());
        foreach (var p in w.Precipitation)
            parts.Add(p.ToString());
        if (w.Obscuration.HasValue)
            parts.Add(w.Obscuration.Value.ToString());
        if (w.Other.HasValue)
            parts.Add(w.Other.Value.ToString());
        if (w.IsRecent)
            parts.Add("(recent)");

        return parts.Count > 0 ? string.Join(" ", parts) : "unknown";
    }

    private static string FormatForecastPeriod(ForecastPeriod p)
    {
        var sb = new StringBuilder();

        sb.Append($"  {p.ChangeType}");
        if (p.ValidFromUtc.HasValue) sb.Append($" from {p.ValidFromUtc.Value:ddd yyyy-MM-dd HH:mm} UTC");
        if (p.ValidToUtc.HasValue)   sb.Append($" to {p.ValidToUtc.Value:ddd yyyy-MM-dd HH:mm} UTC");
        sb.Append(":");

        if (p.Cavok)
        {
            sb.Append(" CAVOK");
        }
        else
        {
            if (p.WindSpeedKt.HasValue)
            {
                sb.Append(p.WindIsVariable
                    ? $" wind VRB{p.WindSpeedKt} kt"
                    : $" wind {p.WindDirectionDeg:000}° {p.WindSpeedKt} kt");
                if (p.WindGustKt.HasValue) sb.Append($" G{p.WindGustKt} kt");
            }

            if (p.VisibilityStatuteMiles.HasValue)
                sb.Append($" vis {p.VisibilityStatuteMiles.Value:0.##} SM");

            foreach (var layer in p.SkyLayers)
            {
                sb.Append($" {FormatCoverage(layer.Coverage)}");
                if (layer.HeightFeet.HasValue) sb.Append($"/{layer.HeightFeet}ft");
            }

            foreach (var w in p.WeatherPhenomena)
                sb.Append($" {FormatPhenomenon(w)}");
        }

        return sb.ToString();
    }
}
