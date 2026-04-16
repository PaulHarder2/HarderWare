using System.Text;
using WxInterp;

namespace WxReport.Svc;

/// <summary>
/// Converts a <see cref="WeatherSnapshot"/> into a structured plain-text
/// description that can be supplied to the Claude API as a data payload.
/// </summary>
public static class SnapshotDescriber
{
    /// <summary>
    /// Converts <paramref name="snap"/> into a structured plain-text block
    /// used as the data payload in the Claude API prompt.
    /// Covers current date/time, station, observation time, wind, visibility,
    /// sky conditions, weather phenomena, temperature, dew point, altimeter,
    /// and forecast periods.
    /// </summary>
    /// <param name="snap">The weather snapshot to describe.</param>
    /// <param name="tz">Timezone used to localise the current time and the observation time in the output.</param>
    /// <param name="units">Unit preferences controlling how temperatures, pressure, and wind speeds are formatted. Defaults to US customary when <see langword="null"/>.</param>
    /// <returns>A multi-line plain-text string describing all reported conditions.</returns>
    public static string Describe(WeatherSnapshot snap, TimeZoneInfo tz, UnitPreferences? units = null)
    {
        units ??= new UnitPreferences();
        var sb = new StringBuilder();

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);

        sb.AppendLine($"Current date/time: {localNow:dddd, yyyy-MM-dd HH:mm} local / {DateTime.UtcNow:HH:mm} UTC");
        sb.AppendLine($"Forecast location: {snap.LocalityName}");

        if (!snap.ObservationAvailable)
        {
            sb.AppendLine($"Current observation: NOT AVAILABLE — {snap.ObservationUnavailableNote}");
            AppendForecastSections(sb, snap, units);
            return sb.ToString();
        }

        var localObs = TimeZoneInfo.ConvertTimeFromUtc(snap.ObservationTimeUtc, tz);

        sb.AppendLine($"Station ICAO: {snap.StationIcao}");
        if (snap.StationMunicipality is not null)
            sb.AppendLine($"Station city: {snap.StationMunicipality}");
        if (snap.StationName is not null)
            sb.AppendLine($"Station name: {snap.StationName}");
        if (snap.ObservationDistanceKm is double kmDist)
            sb.AppendLine($"Station distance from recipient: {kmDist * 0.621371:0.#} statute miles");
        sb.AppendLine($"Observed: {localObs:dddd, yyyy-MM-dd HH:mm} local / {snap.ObservationTimeUtc:HH:mm} UTC{(snap.IsAutomated ? " (automated)" : "")}");

        // Wind
        if (snap.WindIsVariable && snap.WindSpeedKt is 0 or null)
            sb.AppendLine("Wind: variable/calm");
        else if (snap.WindIsVariable)
            sb.AppendLine($"Wind: variable at {FormatWindSpeed(snap.WindSpeedKt, units)}{GustStr(snap.WindGustKt, units)}");
        else if (snap.WindSpeedKt is 0 or null)
            sb.AppendLine("Wind: calm");
        else
            sb.AppendLine($"Wind: {snap.WindDirectionDeg:000}° at {FormatWindSpeed(snap.WindSpeedKt, units)}{GustStr(snap.WindGustKt, units)}");

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

        // Temperature / relative humidity
        if (snap.TemperatureFahrenheit.HasValue)
            sb.AppendLine($"Temperature: {FormatTemp(snap.TemperatureFahrenheit, snap.TemperatureCelsius, units)}");
        if (snap.TemperatureCelsius.HasValue && snap.DewPointCelsius.HasValue)
            sb.AppendLine($"Humidity: {ComputeRelativeHumidity(snap.TemperatureCelsius.Value, snap.DewPointCelsius.Value):0}%");

        // Pressure
        if (snap.AltimeterInHg.HasValue)
            sb.AppendLine($"Pressure: {FormatPressure(snap.AltimeterInHg.Value, units)}");

        AppendForecastSections(sb, snap, units);

        return sb.ToString();
    }

    private static void AppendForecastSections(StringBuilder sb, WeatherSnapshot snap, UnitPreferences units)
    {
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

        // GFS model forecast
        if (snap.GfsForecast is { Days.Count: > 0 } gfs)
        {
            sb.AppendLine($"GFS model forecast (run: {gfs.ModelRunUtc:ddd yyyy-MM-dd HH}Z):");
            foreach (var day in gfs.Days)
                sb.AppendLine(FormatGfsDay(day, units));
        }
    }

    // ── GFS helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Formats one <see cref="GfsDailyForecast"/> as a single indented line for
    /// inclusion in the Claude prompt.  Includes high/low temperature, maximum
    /// wind speed and direction, cloud cover, CAPE (with a qualitative label),
    /// and precipitation rate (when present).
    /// </summary>
    /// <param name="day">The daily GFS forecast to format.</param>
    /// <param name="units">Unit preferences controlling temperature and wind speed formatting.</param>
    /// <returns>An indented one-line string describing the day's forecast.</returns>
    private static string FormatGfsDay(GfsDailyForecast day, UnitPreferences units)
    {
        var sb = new StringBuilder();
        sb.Append($"  {day.Date:ddd MM/dd}:");

        if (day.HighTempF.HasValue && day.LowTempF.HasValue)
            sb.Append($" High {FormatTemp(day.HighTempF, day.HighTempC, units)}," +
                      $" Low {FormatTemp(day.LowTempF, day.LowTempC, units)};");

        if (day.MaxWindSpeedKt.HasValue)
        {
            var dir = day.DominantWindDirDeg.HasValue ? $" from {DegreesToCompass(day.DominantWindDirDeg.Value)}" : "";
            sb.Append($" max wind {FormatWindSpeed(day.MaxWindSpeedKt.Value, units)}{dir};");
        }

        if (day.MaxCloudCoverPct.HasValue)
            sb.Append($" clouds up to {day.MaxCloudCoverPct.Value:0}%;");

        if (day.MaxCapeJKg.HasValue)
            sb.Append($" CAPE {day.MaxCapeJKg.Value:0} J/kg ({CapeLabel(day.MaxCapeJKg.Value)});");

        if (day.MaxPrecipRateMmHr.HasValue)
            sb.Append($" precipitation up to {day.MaxPrecipRateMmHr.Value:0.0} mm/hr");
        else
            sb.Append(" no significant precipitation");

        return sb.ToString().TrimEnd(';');
    }

    /// <summary>Converts a wind direction in degrees to a 16-point compass label.</summary>
    /// <param name="deg">Wind direction in degrees true.</param>
    /// <returns>A compass label such as <c>"NNW"</c> or <c>"SW"</c>.</returns>
    private static string DegreesToCompass(int deg)
    {
        string[] dirs = ["N","NNE","NE","ENE","E","ESE","SE","SSE","S","SSW","SW","WSW","W","WNW","NW","NNW"];
        return dirs[(int)Math.Round(((deg % 360 + 360) % 360) / 22.5) % 16];
    }

    /// <summary>Returns a qualitative CAPE label to help Claude choose appropriate language.</summary>
    /// <param name="capeJKg">Surface-based CAPE in J/kg.</param>
    /// <returns>A label string: <c>"low"</c>, <c>"moderate"</c>, <c>"significant"</c>, or <c>"extreme"</c>.</returns>
    private static string CapeLabel(float capeJKg) => capeJKg switch
    {
        < 500  => "low",
        < 1000 => "moderate",
        < 2000 => "significant",
        _      => "extreme",
    };

    // ── unit-aware formatting helpers ─────────────────────────────────────────

    /// <summary>Formats a temperature value in the recipient's preferred unit.</summary>
    /// <param name="tempF">Temperature in °F, or <see langword="null"/>.</param>
    /// <param name="tempC">Temperature in °C, or <see langword="null"/>.</param>
    /// <param name="units">Unit preferences.</param>
    /// <returns>A formatted string such as <c>"77°F"</c> or <c>"25°C"</c>.</returns>
    private static string FormatTemp(double? tempF, double? tempC, UnitPreferences units)
    {
        if (units.Temperature == "C")
            return tempC.HasValue ? $"{tempC.Value:0}°C" : "?°C";
        return tempF.HasValue ? $"{tempF.Value:0}°F" : "?°F";
    }

    /// <summary>Overload accepting <see langword="float"/> temperatures as stored in <see cref="GfsDailyForecast"/>.</summary>
    /// <param name="tempF">Temperature in °F, or <see langword="null"/>.</param>
    /// <param name="tempC">Temperature in °C, or <see langword="null"/>.</param>
    /// <param name="units">Unit preferences.</param>
    /// <returns>A formatted string such as <c>"87°F"</c> or <c>"31°C"</c>.</returns>
    private static string FormatTemp(float? tempF, float? tempC, UnitPreferences units)
        => FormatTemp((double?)tempF, (double?)tempC, units);

    /// <summary>
    /// Computes relative humidity from temperature and dew point using the Magnus formula.
    /// Returns a value in the range 0–100.
    /// </summary>
    /// <param name="tempC">Air temperature in degrees Celsius.</param>
    /// <param name="dewPointC">Dew-point temperature in degrees Celsius.</param>
    /// <returns>Relative humidity as a percentage (0–100).</returns>
    private static double ComputeRelativeHumidity(double tempC, double dewPointC)
    {
        const double a = 17.625;
        const double b = 243.04;
        return 100.0 * Math.Exp(a * dewPointC / (b + dewPointC))
                     / Math.Exp(a * tempC     / (b + tempC));
    }

    /// <summary>Formats a wind speed from knots in the recipient's preferred speed unit.</summary>
    /// <param name="kt">Speed in knots.</param>
    /// <param name="units">Unit preferences.</param>
    /// <returns>A formatted string such as <c>"12 mph"</c> or <c>"19 kph"</c>.</returns>
    private static string FormatWindSpeed(float kt, UnitPreferences units)
        => units.WindSpeed == "kph"
            ? $"{kt * 1.852:0} kph"
            : $"{kt * 1.15078:0} mph";

    /// <summary>Overload accepting a nullable <see langword="int"/> knot value.</summary>
    /// <param name="kt">Speed in knots, or <see langword="null"/>.</param>
    /// <param name="units">Unit preferences.</param>
    /// <returns>A formatted string, or <c>"0 mph"</c> / <c>"0 kph"</c> when <paramref name="kt"/> is <see langword="null"/>.</returns>
    private static string FormatWindSpeed(int? kt, UnitPreferences units)
        => FormatWindSpeed((float)(kt ?? 0), units);

    /// <summary>Returns a formatted gust string to append to a wind line, or an empty string if no gust was reported.</summary>
    /// <param name="gust">Gust speed in knots, or <see langword="null"/> if not reported.</param>
    /// <param name="units">Unit preferences.</param>
    /// <returns>A string such as <c>", gusting 40 mph"</c> or <c>", gusting 64 kph"</c>, or <c>""</c> if no gust.</returns>
    private static string GustStr(int? gust, UnitPreferences units) =>
        gust.HasValue ? $", gusting {FormatWindSpeed(gust, units)}" : "";

    /// <summary>Formats a pressure value in the recipient's preferred pressure unit.</summary>
    /// <param name="inHg">Altimeter setting in inches of mercury.</param>
    /// <param name="units">Unit preferences.</param>
    /// <returns>A formatted string such as <c>"30.08 inHg"</c> or <c>"101.9 kPa"</c>.</returns>
    private static string FormatPressure(double inHg, UnitPreferences units)
        => units.Pressure == "kPa"
            ? $"{inHg * 3.38639:0.0} kPa"
            : $"{inHg:0.00} inHg";

    /// <summary>Returns a human-readable English description of a sky coverage code.</summary>
    /// <param name="c">The coverage enum value to format.</param>
    /// <returns>A display string such as <c>"Few"</c>, <c>"Scattered"</c>, or <c>"Overcast"</c>.</returns>
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

    /// <summary>
    /// Formats a weather phenomenon as a space-separated string of its
    /// intensity, descriptor, precipitation types, obscuration, other, and
    /// <c>(recent)</c> flag in that order.
    /// </summary>
    /// <param name="w">The weather phenomenon to format.</param>
    /// <returns>A display string such as <c>"Heavy Showers Rain"</c>, or <c>"unknown"</c> if no components are present.</returns>
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

    /// <summary>
    /// Formats a single TAF change period as a one-line string for inclusion
    /// in the Claude prompt.  Includes the change type, validity window (UTC),
    /// wind, visibility, sky layers, and weather phenomena.
    /// </summary>
    /// <param name="p">The forecast period to format.</param>
    /// <returns>A compact one-line string representation of the period.</returns>
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
