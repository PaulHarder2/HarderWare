using MetarParser.Data.Entities;

namespace MetarParser.Data;

/// <summary>
/// Maps a decoded <see cref="MetarReport"/> to a <see cref="MetarRecord"/> entity
/// graph ready for insertion into the database.
/// </summary>
public static class MetarRecordMapper
{
    /// <summary>
    /// Converts a parsed <see cref="MetarReport"/> into a <see cref="MetarRecord"/>
    /// with fully populated child collections for sky conditions, weather phenomena,
    /// and runway visual ranges.
    /// </summary>
    /// <param name="report">
    /// The decoded report returned by <see cref="MetarParser.MetarParser.Parse"/>.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A new <see cref="MetarRecord"/> instance whose <see cref="MetarRecord.Id"/> is
    /// zero (unset) and whose <see cref="MetarRecord.ReceivedUtc"/> is set to the
    /// current UTC time.  The entity has not been saved to the database.
    /// </returns>
    public static MetarRecord ToEntity(MetarReport report)
    {
        var record = new MetarRecord
        {
            ReportType = report.ReportType,
            StationIcao = report.Station,
            ObservationUtc = InferObservationUtc(report.Day, report.Hour, report.Minute, DateTime.UtcNow),
            IsAuto = report.IsAuto,
            IsCorrection = report.IsCorrection,
            RawReport = report.Raw,
            ReceivedUtc = DateTime.UtcNow,
        };

        MapWind(report, record);
        MapVisibility(report, record);
        MapTemperature(report, record);
        MapAltimeter(report, record);
        MapSkyConditions(report, record);
        MapWeatherPhenomena(report, record);
        MapRunwayVisualRanges(report, record);

        record.Remarks = report.Remarks;

        return record;
    }

    // ── private mapping helpers ──────────────────────────────────────────────

    /// <summary>
    /// Copies wind group fields from <paramref name="report"/> to <paramref name="record"/>.
    /// All wind columns remain <see langword="null"/> when no wind group was decoded.
    /// </summary>
    /// <param name="report">Source parsed report.</param>
    /// <param name="record">Target entity to populate in place.</param>
    /// <sideeffects>Mutates wind-related properties of <paramref name="record"/>.</sideeffects>
    private static void MapWind(MetarReport report, MetarRecord record)
    {
        if (report.Wind is not { } w) return;
        record.WindDirection = w.Direction;
        record.WindIsVariable = w.IsVariable;
        record.WindSpeed = w.Speed;
        record.WindGust = w.Gust;
        record.WindUnit = w.Unit;
        record.WindVariableFrom = w.VariableFrom;
        record.WindVariableTo = w.VariableTo;
    }

    /// <summary>
    /// Copies visibility fields from <paramref name="report"/> to <paramref name="record"/>.
    /// All visibility columns remain at their defaults when no visibility group was decoded.
    /// </summary>
    /// <param name="report">Source parsed report.</param>
    /// <param name="record">Target entity to populate in place.</param>
    /// <sideeffects>Mutates visibility-related properties of <paramref name="record"/>.</sideeffects>
    private static void MapVisibility(MetarReport report, MetarRecord record)
    {
        if (report.Visibility is not { } v) return;
        record.VisibilityCavok = v.Cavok;
        record.VisibilityM = v.DistanceMeters;
        record.VisibilityStatuteMiles = v.DistanceStatuteMiles;
        record.VisibilityLessThan = v.LessThan;
    }

    /// <summary>
    /// Copies temperature and dew-point fields from <paramref name="report"/> to
    /// <paramref name="record"/>.
    /// Dew-point is stored as <see langword="null"/> when it was missing from the report
    /// (represented as <see cref="double.NaN"/> in the parsed model).
    /// </summary>
    /// <param name="report">Source parsed report.</param>
    /// <param name="record">Target entity to populate in place.</param>
    /// <sideeffects>Mutates temperature-related properties of <paramref name="record"/>.</sideeffects>
    private static void MapTemperature(MetarReport report, MetarRecord record)
    {
        if (report.Temperature is not { } t) return;
        record.AirTemperatureCelsius = t.Air;
        record.DewPointCelsius = double.IsNaN(t.DewPoint) ? null : t.DewPoint;
    }

    /// <summary>
    /// Copies altimeter setting fields from <paramref name="report"/> to <paramref name="record"/>.
    /// All altimeter columns remain <see langword="null"/> when no altimeter group was decoded.
    /// </summary>
    /// <param name="report">Source parsed report.</param>
    /// <param name="record">Target entity to populate in place.</param>
    /// <sideeffects>Mutates altimeter-related properties of <paramref name="record"/>.</sideeffects>
    private static void MapAltimeter(MetarReport report, MetarRecord record)
    {
        if (report.Altimeter is not { } a) return;
        record.AltimeterValue = a.Value;
        record.AltimeterUnit = a.Unit;
    }

    /// <summary>
    /// Converts each <see cref="SkyCondition"/> in <paramref name="report"/> to a
    /// <see cref="MetarSkyCondition"/> child entity and appends it to
    /// <see cref="MetarRecord.SkyConditions"/>.
    /// Also stores the raw token string in <see cref="MetarRecord.RawSkyConditions"/>.
    /// </summary>
    /// <param name="report">Source parsed report.</param>
    /// <param name="record">Target entity whose <see cref="MetarRecord.SkyConditions"/> collection is populated.</param>
    /// <sideeffects>Adds <see cref="MetarSkyCondition"/> children to <paramref name="record"/> and sets <see cref="MetarRecord.RawSkyConditions"/>.</sideeffects>
    private static void MapSkyConditions(MetarReport report, MetarRecord record)
    {
        if (report.Sky.Count == 0) return;

        record.RawSkyConditions = string.Join(" ", report.Sky.Select(s => s.ToString()));

        for (int i = 0; i < report.Sky.Count; i++)
        {
            var sky = report.Sky[i];
            record.SkyConditions.Add(new MetarSkyCondition
            {
                Cover = sky.Cover,
                HeightFeet = sky.HeightFeet,
                CloudType = sky.CloudType,
                IsVerticalVisibility = sky.IsVerticalVisibility,
                SortOrder = i,
            });
        }
    }

    /// <summary>
    /// Converts present and recent weather phenomena from <paramref name="report"/>
    /// to <see cref="MetarWeatherPhenomenon"/> child entities.
    /// Also stores the raw token string in <see cref="MetarRecord.RawWeatherPhenomena"/>.
    /// </summary>
    /// <param name="report">Source parsed report.</param>
    /// <param name="record">Target entity whose <see cref="MetarRecord.WeatherPhenomena"/> collection is populated.</param>
    /// <sideeffects>Adds <see cref="MetarWeatherPhenomenon"/> children to <paramref name="record"/> and sets <see cref="MetarRecord.RawWeatherPhenomena"/>.</sideeffects>
    private static void MapWeatherPhenomena(MetarReport report, MetarRecord record)
    {
        var allPhenomena = report.PresentWeather
            .Select(w => (kind: "Present", wx: w))
            .Concat(report.RecentWeather.Select(w => (kind: "Recent", wx: w)))
            .ToList();

        if (allPhenomena.Count == 0) return;

        var rawParts = report.PresentWeather.Select(w => w.ToString())
            .Concat(report.RecentWeather.Select(w => "RE" + w));
        record.RawWeatherPhenomena = string.Join(" ", rawParts);

        for (int i = 0; i < allPhenomena.Count; i++)
        {
            var (kind, wx) = allPhenomena[i];
            record.WeatherPhenomena.Add(new MetarWeatherPhenomenon
            {
                PhenomenonKind = kind,
                Intensity = wx.Intensity,
                Descriptor = wx.Descriptor,
                Precipitation = wx.Precipitation.Count > 0
                                     ? string.Join(",", wx.Precipitation)
                                     : null,
                Obscuration = wx.Obscuration,
                OtherPhenomenon = wx.Other,
                SortOrder = i,
            });
        }
    }

    /// <summary>
    /// Converts each <see cref="RunwayVisualRange"/> in <paramref name="report"/> to a
    /// <see cref="MetarRunwayVisualRange"/> child entity.
    /// Also stores the raw token string in <see cref="MetarRecord.RawRunwayVisualRange"/>.
    /// </summary>
    /// <param name="report">Source parsed report.</param>
    /// <param name="record">Target entity whose <see cref="MetarRecord.RunwayVisualRanges"/> collection is populated.</param>
    /// <sideeffects>Adds <see cref="MetarRunwayVisualRange"/> children to <paramref name="record"/> and sets <see cref="MetarRecord.RawRunwayVisualRange"/>.</sideeffects>
    private static void MapRunwayVisualRanges(MetarReport report, MetarRecord record)
    {
        if (report.Rvr.Count == 0) return;

        record.RawRunwayVisualRange = string.Join(" ", report.Rvr.Select(r => r.ToString()));

        foreach (var rvr in report.Rvr)
        {
            record.RunwayVisualRanges.Add(new MetarRunwayVisualRange
            {
                Runway = rvr.Runway,
                MeanMeters = rvr.MeanMeters,
                MinMeters = rvr.MinMeters,
                MaxMeters = rvr.MaxMeters,
                BelowMinimum = rvr.BelowMinimum,
                AboveMaximum = rvr.AboveMaximum,
                Trend = rvr.Trend?.ToString(),
            });
        }
    }

    /// <summary>
    /// Infers a full UTC <see cref="DateTime"/> from the day, hour, and minute values
    /// encoded in a METAR date/time group.
    /// Because METAR reports include only the day-of-month, hour, and minute (omitting
    /// year and month), the full date is reconstructed by assuming the reported day
    /// belongs to the most recent calendar month in which that day number occurred
    /// relative to the current UTC clock.
    /// If the reported day is greater than today's UTC day, the observation is assumed
    /// to belong to the previous calendar month (rolling back to December of the prior
    /// year if necessary).
    /// </summary>
    /// <param name="day">Day-of-month from the METAR date/time group (1–31).</param>
    /// <param name="hour">UTC hour from the METAR date/time group (0–23).</param>
    /// <param name="minute">UTC minute from the METAR date/time group (0–59).</param>
    /// <param name="referenceTime">
    /// The UTC clock time to use as "now" when inferring the month and year.
    /// Pass <see cref="DateTime.UtcNow"/> in production; pass a fixed value in tests.
    /// </param>
    /// <returns>
    /// A <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/> whose year and month
    /// have been inferred, and whose day, hour, and minute match the report values.
    /// </returns>
    internal static DateTime InferObservationUtc(int day, int hour, int minute, DateTime referenceTime)
    {
        int year = referenceTime.Year;
        int month = referenceTime.Month;

        if (day > referenceTime.Day)
        {
            month--;
            if (month == 0) { month = 12; year--; }
        }

        return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);
    }
}