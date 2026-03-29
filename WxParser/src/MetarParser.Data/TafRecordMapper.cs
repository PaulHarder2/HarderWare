using MetarParser.Data.Entities;
using TafParser;

namespace MetarParser.Data;

/// <summary>
/// Maps a decoded <see cref="TafReport"/> to a <see cref="TafRecord"/> entity
/// graph ready for insertion into the database.
/// </summary>
public static class TafRecordMapper
{
    /// <summary>
    /// Converts a parsed <see cref="TafReport"/> into a <see cref="TafRecord"/>
    /// with fully populated child collections.
    /// </summary>
    /// <param name="report">The decoded TAF report to convert.  Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A new <see cref="TafRecord"/> whose <see cref="TafRecord.Id"/> is zero (unset)
    /// and whose <see cref="TafRecord.ReceivedUtc"/> is set to the current UTC time.
    /// The entity has not been saved to the database.
    /// </returns>
    public static TafRecord ToEntity(TafReport report)
    {
        var now = DateTime.UtcNow;

        var issuanceUtc = MetarRecordMapper.InferObservationUtc(
            report.IssuanceDay, report.IssuanceHour, report.IssuanceMinute, now);

        // Infer validity period dates relative to the issuance date.
        var validFromUtc = InferValidityDate(report.ValidFromDay, report.ValidFromHour, issuanceUtc);
        var validToUtc   = InferValidityDate(report.ValidToDay,   report.ValidToHour,   validFromUtc);

        var record = new TafRecord
        {
            ReportType   = report.ReportType,
            StationIcao  = report.Station,
            IssuanceUtc  = issuanceUtc,
            ValidFromUtc = validFromUtc,
            ValidToUtc   = validToUtc,
            RawReport    = report.Raw,
            ReceivedUtc  = now,
        };

        record.ChangePeriods.Add(MapBasePeriod(report, validFromUtc, validToUtc));
        MapChangePeriods(report, record, issuanceUtc);

        return record;
    }

    // ── base period ──────────────────────────────────────────────────────────

    /// <summary>
    /// Constructs the BASE (initial conditions) change period for a TAF, covering the full
    /// validity window.  All fields from the TAF's top-level wind, visibility, sky, and
    /// weather groups are copied into the returned record.
    /// </summary>
    /// <param name="report">The parsed TAF report providing the base-period conditions.</param>
    /// <param name="validFromUtc">Inferred UTC start of the TAF validity window.</param>
    /// <param name="validToUtc">Inferred UTC end of the TAF validity window.</param>
    /// <returns>A new <see cref="TafChangePeriodRecord"/> with <see cref="TafChangePeriodRecord.ChangeType"/> set to <c>"BASE"</c>.</returns>
    private static TafChangePeriodRecord MapBasePeriod(TafReport report, DateTime validFromUtc, DateTime validToUtc)
    {
        var base_ = new TafChangePeriodRecord
        {
            ChangeType   = "BASE",
            ValidFromUtc = validFromUtc,
            ValidToUtc   = validToUtc,
            SortOrder    = 0,
        };

        if (report.Wind is { } w)
        {
            base_.WindDirection  = w.Direction;
            base_.WindIsVariable = w.IsVariable;
            base_.WindSpeed      = w.Speed;
            base_.WindGust       = w.Gust;
            base_.WindUnit       = w.Unit;
        }

        if (report.Visibility is { } v)
        {
            base_.VisibilityCavok        = v.Cavok;
            base_.VisibilityM            = v.DistanceMeters;
            base_.VisibilityStatuteMiles = v.DistanceStatuteMiles;
            base_.VisibilityLessThan     = v.LessThan;
        }

        if (report.Sky.Count > 0)
        {
            base_.RawSkyConditions = string.Join(" ", report.Sky.Select(s => s.ToString()));
            for (int i = 0; i < report.Sky.Count; i++)
            {
                var s = report.Sky[i];
                base_.SkyConditions.Add(new TafChangePeriodSky
                {
                    Cover                = s.Cover,
                    HeightFeet           = s.HeightFeet,
                    CloudType            = s.CloudType,
                    IsVerticalVisibility = s.IsVerticalVisibility,
                    SortOrder            = i,
                });
            }
        }

        if (report.Weather.Count > 0)
        {
            base_.RawWeather = string.Join(" ", report.Weather.Select(wx => wx.ToString()));
            for (int i = 0; i < report.Weather.Count; i++)
            {
                var wx = report.Weather[i];
                base_.WeatherPhenomena.Add(new TafChangePeriodWeather
                {
                    Intensity       = wx.Intensity,
                    Descriptor      = wx.Descriptor,
                    Precipitation   = wx.Precipitation.Count > 0 ? string.Join(",", wx.Precipitation) : null,
                    Obscuration     = wx.Obscuration,
                    OtherPhenomenon = wx.Other,
                    SortOrder       = i,
                });
            }
        }

        return base_;
    }

    /// <summary>
    /// Appends a <see cref="TafChangePeriodRecord"/> to <paramref name="record"/>
    /// for each BECMG, TEMPO, FM, or PROB change group in the TAF.
    /// Validity timestamps are inferred relative to <paramref name="issuanceUtc"/>.
    /// </summary>
    /// <param name="report">The parsed TAF report whose change groups are mapped.</param>
    /// <param name="record">The parent TAF entity whose <see cref="TafRecord.ChangePeriods"/> collection receives the new records.</param>
    /// <param name="issuanceUtc">The inferred UTC issuance time, used as the reference for date inference.</param>
    /// <sideeffects>Appends <see cref="TafChangePeriodRecord"/> children to <paramref name="record"/>.</sideeffects>
    private static void MapChangePeriods(TafReport report, TafRecord record, DateTime issuanceUtc)
    {
        for (int i = 0; i < report.ChangePeriods.Count; i++)
        {
            var p = report.ChangePeriods[i];

            DateTime? fromUtc = p.FromDay.HasValue && p.FromHour.HasValue
                ? InferValidityDate(p.FromDay.Value, p.FromHour.Value, issuanceUtc)
                : null;

            DateTime? toUtc = p.ToDay.HasValue && p.ToHour.HasValue
                ? InferValidityDate(p.ToDay.Value, p.ToHour.Value, issuanceUtc)
                : null;

            var period = new TafChangePeriodRecord
            {
                ChangeType   = p.ChangeType,
                ValidFromUtc = fromUtc,
                ValidToUtc   = toUtc,
                SortOrder    = i + 1,
            };

            if (p.Wind is { } w)
            {
                period.WindDirection  = w.Direction;
                period.WindIsVariable = w.IsVariable;
                period.WindSpeed      = w.Speed;
                period.WindGust       = w.Gust;
                period.WindUnit       = w.Unit;
            }

            if (p.Visibility is { } v)
            {
                period.VisibilityCavok        = v.Cavok;
                period.VisibilityM            = v.DistanceMeters;
                period.VisibilityStatuteMiles = v.DistanceStatuteMiles;
                period.VisibilityLessThan     = v.LessThan;
            }

            if (p.Sky.Count > 0)
            {
                period.RawSkyConditions = string.Join(" ", p.Sky.Select(s => s.ToString()));
                for (int j = 0; j < p.Sky.Count; j++)
                {
                    var s = p.Sky[j];
                    period.SkyConditions.Add(new TafChangePeriodSky
                    {
                        Cover                = s.Cover,
                        HeightFeet           = s.HeightFeet,
                        CloudType            = s.CloudType,
                        IsVerticalVisibility = s.IsVerticalVisibility,
                        SortOrder            = j,
                    });
                }
            }

            if (p.Weather.Count > 0)
            {
                period.RawWeather = string.Join(" ", p.Weather.Select(w => w.ToString()));
                for (int j = 0; j < p.Weather.Count; j++)
                {
                    var wx = p.Weather[j];
                    period.WeatherPhenomena.Add(new TafChangePeriodWeather
                    {
                        Intensity       = wx.Intensity,
                        Descriptor      = wx.Descriptor,
                        Precipitation   = wx.Precipitation.Count > 0 ? string.Join(",", wx.Precipitation) : null,
                        Obscuration     = wx.Obscuration,
                        OtherPhenomenon = wx.Other,
                        SortOrder       = j,
                    });
                }
            }

            record.ChangePeriods.Add(period);
        }
    }

    // ── date inference ───────────────────────────────────────────────────────

    /// <summary>
    /// Infers the full UTC <see cref="DateTime"/> for a TAF validity boundary
    /// given the day and hour from the DDHH/DDHH group.
    /// The month and year are taken from <paramref name="reference"/>.
    /// If the boundary day is less than the reference day, the boundary is
    /// assumed to belong to the following month (e.g. a TAF issued on the 30th
    /// with a validity end on the 1st).
    /// A validity hour of 24 is normalised to 00:00 of the next day.
    /// </summary>
    internal static DateTime InferValidityDate(int day, int hour, DateTime reference)
    {
        int year  = reference.Year;
        int month = reference.Month;

        // Validity end day may roll into the next month.
        if (day < reference.Day)
        {
            month++;
            if (month == 13) { month = 1; year++; }
        }

        // Hour 24 means midnight at the start of the next day.
        if (hour == 24)
        {
            var midnight = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            return midnight.AddDays(1);
        }

        return new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc);
    }
}
