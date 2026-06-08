using System.Globalization;
using System.Text;

using MetarParser.Data.Entities;

using WxInterp;

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// WX-129 deterministic renderer: turns the unit-neutral structured report
/// (WX-128) into one recipient's HTML email body — applying that recipient's
/// units, precipitation unit (WX-142), name, and language — with <b>no LLM
/// call</b>.  This is the cheap inner step that makes WX-123's one-Claude-call-
/// per-locality economics work: the expensive reasoning ran once for the
/// locality; this fans it out per recipient.
///
/// <para>
/// Layout follows the established report shape (data in tables, judgment in
/// prose, decided 2026-06-08): the Current Conditions <b>table</b> is rebuilt
/// deterministically from the shared observation, the Extended Forecast
/// <b>grid</b> (one row per local calendar day) from the reconciled
/// <see cref="ForecastSnapshotBody"/>, and the change-summary band and closing
/// are the language-keyed narrative prose with <see cref="ReportTokens"/> tokens
/// substituted into the recipient's units/locale.  The grid's per-day Conditions
/// cell is a deterministic phrase composed from sky + precip + severe state.
/// </para>
///
/// <para>
/// Output is the inner HTML for the <c>&lt;body&gt;</c> only; the DOCTYPE/wrapper,
/// footer, and meteogram insertion stay in <c>ReportWorker</c> (WX-130 wires this
/// renderer into the send path).  Numbers format with US/period-decimal
/// conventions for every recipient until WX-138 wires in <c>Recipient.NumberFormat</c>;
/// date and weekday/month names localize to the recipient's language.
/// </para>
/// </summary>
public static class StructuredReportRenderer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Renders <paramref name="report"/> into <paramref name="recipient"/>'s inner
    /// HTML body.  <paramref name="finalSnapshot"/> drives the Extended Forecast
    /// grid, <paramref name="observation"/> the Current Conditions table, and
    /// <paramref name="localityTz"/> localizes per-day bucketing and instant
    /// rendering.  <paramref name="isUnscheduled"/> selects the header's
    /// unscheduled-update line; the change-summary band itself renders whenever the
    /// narrative carries one.  (A recipient's first contact is a separate
    /// welcome-only email — see <see cref="RenderWelcome"/>; this method always
    /// renders a weather report.)
    /// </summary>
    public static string Render(
        StructuredReportBody report,
        ForecastSnapshotBody finalSnapshot,
        WeatherSnapshot observation,
        Recipient recipient,
        TimeZoneInfo localityTz,
        bool isUnscheduled)
    {
        // The narrative map and ReportVocabulary key on ISO 639-1 (en, es).
        // ToIetfTag already collapses to the two-letter code, but normalize
        // defensively so a future regional tag (e.g. "es-419") can't silently
        // miss the narrative and fall back.
        var lang = LanguageHelper.ToIetfTag(recipient.Language).Split('-')[0].ToLowerInvariant();
        var sections = SelectNarrative(report, lang);
        var vocab = ReportVocabulary.ForLanguage(lang);

        string RenderProse(string prose) => HtmlText(ReportTokens.Substitute(
            prose,
            (kind, value) => RenderQuantity(kind, value, recipient),
            instant => RenderInstant(instant, localityTz, vocab.Culture)));

        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");

        AppendHeader(sb, observation, localityTz, vocab, isUnscheduled);

        if (!string.IsNullOrWhiteSpace(sections.ChangeSummary))
            AppendChangeBand(sb, vocab, RenderProse(sections.ChangeSummary!));

        AppendCurrentConditions(sb, observation, recipient, vocab);
        AppendExtendedForecast(sb, finalSnapshot, observation.LocalityName, recipient, localityTz, vocab);
        AppendClosing(sb, vocab, RenderProse(sections.Closing));

        sb.Append("</div>");
        return sb.ToString();
    }

    // ── first-contact welcome (WX-130; standalone welcome-only email) ──────────

    /// <summary>
    /// Renders a recipient's one-time welcome email — a greeting plus a statement
    /// of what to expect (daily reports for <paramref name="localityName"/> at the
    /// locality's <paramref name="scheduledHours"/>, localized to the recipient's
    /// language), with <b>no weather content</b>.  This is the first contact a new
    /// recipient receives; weather reports begin on the locality's normal cadence.
    /// </summary>
    public static string RenderWelcome(
        Recipient recipient,
        string localityName,
        TimeZoneInfo localityTz,
        IReadOnlyList<int> scheduledHours)
    {
        var lang = LanguageHelper.ToIetfTag(recipient.Language).Split('-')[0].ToLowerInvariant();
        var vocab = ReportVocabulary.ForLanguage(lang);
        var body = string.Format(vocab.Culture, vocab.WelcomeFormat, localityName, FormatScheduleTimes(scheduledHours, vocab));

        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");
        sb.Append("<div style=\"background:#1a3a5c;color:#ffffff;text-align:left;padding:20px 24px;border-radius:6px 6px 0 0;\">");
        sb.Append($"<div style=\"font-weight:bold;font-size:22px;\">{HtmlText(localityName)}</div>");
        sb.Append("</div>");
        sb.Append("<div style=\"background:#eef4fb;padding:20px 24px;font-size:15px;line-height:1.5;border-radius:0 0 6px 6px;\">");
        sb.Append($"<strong>{HtmlText(vocab.WelcomeGreeting)}</strong> {HtmlText(body)}");
        sb.Append("</div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Plain-text form of the welcome email (the SMTP fallback), sharing the same vocabulary + schedule formatting as <see cref="RenderWelcome"/> so the two cannot drift.</summary>
    public static string WelcomePlainText(Recipient recipient, string localityName, IReadOnlyList<int> scheduledHours)
    {
        var lang = LanguageHelper.ToIetfTag(recipient.Language).Split('-')[0].ToLowerInvariant();
        var vocab = ReportVocabulary.ForLanguage(lang);
        return $"{vocab.WelcomeGreeting} "
            + string.Format(vocab.Culture, vocab.WelcomeFormat, localityName, FormatScheduleTimes(scheduledHours, vocab));
    }

    /// <summary>Localized "6 AM and 12 PM" list of the recipient's daily send hours, joined with the language's conjunction. Empty when no hours are configured.</summary>
    private static string FormatScheduleTimes(IReadOnlyList<int> hours, ReportVocabulary vocab)
    {
        var times = hours
            .Select(h => new DateTime(2000, 1, 1, h, 0, 0).ToString("h tt", vocab.Culture))
            .ToList();
        if (times.Count == 0)
            return "";
        if (times.Count == 1)
            return times[0];
        return string.Join(", ", times.Take(times.Count - 1)) + $" {vocab.AndConjunction} " + times[^1];
    }

    /// <summary>The recipient's language narrative, falling back to en (then any present) if the recipient's own language is somehow absent. An entirely empty narrative is a precondition violation — <see cref="StructuredReportBody.Validate"/> guarantees at least one language — and throws here, failing the send closed rather than emailing a blank report.</summary>
    private static NarrativeSections SelectNarrative(StructuredReportBody report, string lang)
    {
        if (report.Narrative.TryGetValue(lang, out var s))
            return s;
        if (report.Narrative.TryGetValue("en", out var en))
            return en;
        return report.Narrative.Values.First();
    }

    // ── header ──────────────────────────────────────────────────────────────

    private static void AppendHeader(
        StringBuilder sb, WeatherSnapshot snap, TimeZoneInfo tz, ReportVocabulary vocab, bool isUnscheduled)
    {
        sb.Append("<div style=\"background:#1a3a5c;color:#ffffff;text-align:left;padding:20px 24px;border-radius:6px 6px 0 0;\">");
        sb.Append($"<div style=\"font-weight:bold;font-size:22px;\">{HtmlText(snap.LocalityName)}</div>");
        if (snap.ObservationAvailable)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(snap.ObservationTimeUtc, DateTimeKind.Utc), tz);
            var when = local.ToString("dddd, MMMM d, h:mm tt", vocab.Culture);
            sb.Append($"<div style=\"font-size:14px;color:#c8daea;\">{HtmlText(when)}</div>");
        }
        if (isUnscheduled)
            sb.Append($"<div style=\"font-style:italic;font-size:13px;color:#a0bcd4;\">{HtmlText(vocab.UnscheduledNote)}</div>");
        sb.Append("</div>");
    }

    // ── change-summary band ──────────────────────────────────────────────────

    private static void AppendChangeBand(StringBuilder sb, ReportVocabulary vocab, string renderedProse)
    {
        sb.Append("<div style=\"background:#fef6e4;border-left:4px solid #e8a020;padding:14px 20px;font-size:14px;\">");
        sb.Append($"<strong>{HtmlText(vocab.WhatsChangedLabel)}</strong> {renderedProse}");
        sb.Append("</div>");
    }

    // ── current conditions ────────────────────────────────────────────────────

    private static void AppendCurrentConditions(
        StringBuilder sb, WeatherSnapshot snap, Recipient recipient, ReportVocabulary vocab)
    {
        sb.Append("<div style=\"background:#f7f9fc;padding:20px 24px;\">");
        AppendSectionHeading(sb, vocab.CurrentConditionsHeading);

        // Station-attribution subtitle (WX-130, restored): name the observing
        // station beneath the heading when it differs from the locality, so the
        // reader knows "Current Conditions" came from, e.g., the nearby airport.
        var subtitle = StationSubtitle(snap, vocab);
        if (subtitle is not null)
            sb.Append($"<div style=\"font-size:13px;font-style:italic;color:#6b8fa8;font-weight:normal;margin-top:2px;\">{HtmlText(subtitle)}</div>");

        if (!snap.ObservationAvailable)
        {
            var note = string.IsNullOrWhiteSpace(snap.ObservationUnavailableNote)
                ? vocab.NoObservationNote
                : snap.ObservationUnavailableNote!;
            sb.Append($"<p style=\"font-style:italic;font-size:14px;color:#6b8fa8;margin:12px 0 0;\">{HtmlText(note)}</p>");
            sb.Append("</div>");
            return;
        }

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px;margin-top:8px;\">");
        var row = 0;
        void Row(string label, string value)
        {
            var bg = row++ % 2 == 0 ? "#eaf0f7" : "#ffffff";
            sb.Append($"<tr style=\"background:{bg};\">");
            sb.Append($"<td style=\"padding:6px 10px;font-weight:bold;width:40%;\">{HtmlText(label)}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(value)}</td></tr>");
        }

        Row(vocab.RowSky, SkyPhrase(snap.SkyLayers, vocab));
        Row(vocab.RowVisibility, VisibilityPhrase(snap, vocab));
        Row(vocab.RowWind, WindPhrase(snap, recipient, vocab));
        var weather = WeatherPhrase(snap.WeatherPhenomena, vocab);
        if (weather.Length > 0)
            Row(vocab.RowWeather, weather);
        if (snap.TemperatureCelsius is double tc)
            Row(vocab.RowTemperature, FormatTempC(tc, recipient));
        if (snap.TemperatureCelsius is double t2 && snap.DewPointCelsius is double dp)
            Row(vocab.RowHumidity, $"{Meteorology.RelativeHumidity(t2, dp):0}%");
        if (snap.AltimeterInHg is double inHg)
            Row(vocab.RowPressure, FormatPressureInHg(inHg, recipient));

        sb.Append("</table></div>");
    }

    /// <summary>
    /// The "at &lt;station&gt;" attribution shown under the Current Conditions
    /// heading, or <see langword="null"/> when the observing station IS the
    /// locality (no attribution needed) or no station metadata is available.
    /// Ported from the former reconciler-prompt subtitle logic (WX-130): prefer
    /// "at &lt;municipality&gt;, &lt;airport&gt;", collapse when the airport name
    /// already contains the municipality, fall back to whichever single name exists.
    /// </summary>
    private static string? StationSubtitle(WeatherSnapshot snap, ReportVocabulary vocab)
    {
        var municipality = snap.StationMunicipality;
        var airportName = snap.StationName;
        var locality = snap.LocalityName;

        if (municipality is not null &&
            string.Equals(municipality, locality, StringComparison.OrdinalIgnoreCase))
            return null;

        var at = vocab.StationAtPrefix;
        if (municipality is not null && airportName is not null)
        {
            return airportName.Contains(municipality, StringComparison.OrdinalIgnoreCase)
                ? $"{at} {airportName}"
                : $"{at} {municipality}, {airportName}";
        }
        if (airportName is not null)
            return $"{at} {airportName}";
        if (municipality is not null)
            return $"{at} {municipality}";

        return null;
    }

    // ── extended forecast grid ────────────────────────────────────────────────

    private static void AppendExtendedForecast(
        StringBuilder sb, ForecastSnapshotBody body, string locationName, Recipient recipient, TimeZoneInfo tz, ReportVocabulary vocab)
    {
        var days = AggregateDays(body, tz).ToList();
        if (days.Count == 0)
            return;  // no forecast blocks → omit the section entirely rather than emit a header-only grid

        sb.Append("<div style=\"background:#ffffff;padding:20px 24px;\">");
        AppendSectionHeading(sb, string.Format(vocab.Culture, vocab.ForecastHeadingFormat, locationName));

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px;margin-top:8px;\">");
        sb.Append("<tr style=\"background:#1a3a5c;color:#ffffff;\">");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(vocab.ColDate)}</th>");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(vocab.ColTemperatures)}</th>");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(vocab.ColWind)}</th>");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(vocab.ColConditions)}</th></tr>");

        var row = 0;
        foreach (var day in days)
        {
            var bg = row++ % 2 == 0 ? "#f7f9fc" : "#ffffff";
            var date = day.Date.ToString("ddd MMM d", vocab.Culture);
            var temps = $"{HtmlText(vocab.HighLabel)}: {FormatTempC(day.MaxTempC, recipient)}<br/>{HtmlText(vocab.LowLabel)}: {FormatTempC(day.MinTempC, recipient)}";
            var wind = FormatWindKt(day.MaxWindKt, recipient);
            var cond = ConditionsPhrase(day, vocab);
            sb.Append($"<tr style=\"background:{bg};\">");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(date)}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{temps}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(wind)}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(cond)}</td></tr>");
        }
        sb.Append("</table></div>");
    }

    // ── closing ───────────────────────────────────────────────────────────────

    private static void AppendClosing(StringBuilder sb, ReportVocabulary vocab, string renderedProse)
    {
        sb.Append("<div style=\"background:#f0f4f9;padding:16px 24px;border-top:1px solid #d0dce8;\">");
        sb.Append($"<strong>{HtmlText(vocab.InSummaryLabel)}</strong> {renderedProse}");
        sb.Append("</div>");
    }

    private static void AppendSectionHeading(StringBuilder sb, string text) =>
        sb.Append($"<div style=\"font-weight:bold;font-size:17px;color:#1a3a5c;border-bottom:2px solid #1a3a5c;padding-bottom:4px;\">{HtmlText(text)}</div>");

    // ── per-day aggregation ───────────────────────────────────────────────────

    private sealed record DaySummary(
        DateOnly Date,
        double MaxTempC,
        double MinTempC,
        int MaxWindKt,
        SkyState Sky,
        PrecipExpectation PrecipExpectation,
        PrecipPhenomenon? Phenomenon,
        int PrecipLocalHour,
        bool Severe);

    /// <summary>
    /// Buckets the snapshot's 6-hour blocks into per-local-calendar-day summaries
    /// (mirrors <c>SignificanceGate.DailyHiLoDegF</c>'s grouping): a day's high/low
    /// are the max/min across its blocks, wind the day's peak sustained, sky the
    /// most cloud-significant state, precip the highest expectation (carrying its
    /// phenomenon and the local hour of the first block that reaches it), severe a
    /// logical OR.  Days are returned in chronological order; the first is today.
    /// </summary>
    private static IEnumerable<DaySummary> AggregateDays(ForecastSnapshotBody body, TimeZoneInfo tz)
    {
        var acc = new Dictionary<DateOnly, DaySummary>();
        var order = new List<DateOnly>();

        foreach (var b in body.Blocks)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz);
            var day = DateOnly.FromDateTime(local);

            if (!acc.TryGetValue(day, out var cur))
            {
                order.Add(day);
                acc[day] = new DaySummary(day, b.TemperatureCelsius.Max, b.TemperatureCelsius.Min,
                    b.WindKt.Max, b.SkyState, b.PrecipExpectation, b.PrecipPhenomenon, local.Hour, b.SevereFlag);
                continue;
            }

            var sky = (int)b.SkyState > (int)cur.Sky ? b.SkyState : cur.Sky;
            // Highest expectation wins; the local hour is taken from the first block
            // that reaches the (new) maximum, so the conditions phrase times precip
            // by when it first becomes most likely.
            var (exp, phen, hour) = (int)b.PrecipExpectation > (int)cur.PrecipExpectation
                ? (b.PrecipExpectation, b.PrecipPhenomenon, local.Hour)
                : (cur.PrecipExpectation, cur.Phenomenon, cur.PrecipLocalHour);

            acc[day] = cur with
            {
                MaxTempC = Math.Max(cur.MaxTempC, b.TemperatureCelsius.Max),
                MinTempC = Math.Min(cur.MinTempC, b.TemperatureCelsius.Min),
                MaxWindKt = Math.Max(cur.MaxWindKt, b.WindKt.Max),
                Sky = sky,
                PrecipExpectation = exp,
                Phenomenon = phen,
                PrecipLocalHour = hour,
                Severe = cur.Severe || b.SevereFlag,
            };
        }

        return order.Select(d => acc[d]);
    }

    // ── deterministic phrase composers ────────────────────────────────────────

    private static string SkyPhrase(IReadOnlyList<SkyLayer> layers, ReportVocabulary vocab)
    {
        // Rank by cloudiness, not raw enum ordinal: SkyCoverage orders
        // NoSignificantCloud / NoCloudsDetected ABOVE Broken / Overcast, so a
        // max-ordinal pick would let a "clear"-meaning layer mask real cloud in a
        // mixed layer set. 0 clear · 1 partly · 2 mostly · 3 overcast · 4 obscured.
        var rank = 0;
        int? ceilFeet = null;
        foreach (var l in layers)
        {
            var r = l.Coverage switch
            {
                SkyCoverage.Few or SkyCoverage.Scattered => 1,
                SkyCoverage.Broken => 2,
                SkyCoverage.Overcast => 3,
                SkyCoverage.VerticalVisibility => 4,
                _ => 0,  // Clear, NoSignificantCloud, NoCloudsDetected
            };
            if (r > rank)
                rank = r;
            if ((l.Coverage == SkyCoverage.Broken || l.Coverage == SkyCoverage.Overcast) && l.HeightFeet is int h
                && (ceilFeet is null || h < ceilFeet))
                ceilFeet = h;
        }

        var (phrase, prefixable) = rank switch
        {
            1 => (vocab.SkyPartlyCloudy, false),
            2 => (vocab.SkyMostlyCloudy, true),
            3 => (vocab.SkyOvercast, true),
            4 => (vocab.SkyObscured, false),
            _ => (vocab.SkyClear, false),
        };

        if (prefixable && ceilFeet is int c)
        {
            if (c < 6500)
                return $"{vocab.LowPrefix} {Lower(phrase)}";
            if (c > 20000)
                return $"{vocab.HighPrefix} {Lower(phrase)}";
        }
        return phrase;
    }

    private static string VisibilityPhrase(WeatherSnapshot snap, ReportVocabulary vocab)
    {
        // An obscuration names itself (fog/haze/smoke/mist) rather than a band.
        foreach (var w in snap.WeatherPhenomena)
        {
            var named = ObscurationWord(w.Obscuration, vocab);
            if (named is not null)
                return named;
        }
        if (snap.Cavok)
            return vocab.VisGood;
        if (snap.VisibilityStatuteMiles is not double mi)
            return "—";
        return mi switch
        {
            >= 6 => vocab.VisGood,
            >= 2 => vocab.VisHazy,
            >= 0.5 => vocab.VisReduced,
            _ => vocab.VisPoor,
        };
    }

    private static string WindPhrase(WeatherSnapshot snap, Recipient recipient, ReportVocabulary vocab)
    {
        if ((snap.WindSpeedKt ?? 0) == 0 && snap.WindGustKt is null && !snap.WindIsVariable)
            return vocab.WindCalm;

        var speed = FormatWindKt(snap.WindSpeedKt ?? 0, recipient);
        var dir = snap.WindIsVariable || snap.WindDirectionDeg is null
            ? vocab.WindVariable
            : Meteorology.DegreesToCompass(snap.WindDirectionDeg.Value);
        var gust = snap.WindGustKt is int g ? $", {vocab.WindGusting} {FormatWindKt(g, recipient)}" : "";
        return $"{dir} {vocab.WindAt} {speed}{gust}";
    }

    private static string WeatherPhrase(IReadOnlyList<SnapshotWeather> phenomena, ReportVocabulary vocab)
    {
        var parts = new List<string>();
        foreach (var w in phenomena)
        {
            var p = OneWeatherPhrase(w, vocab);
            if (p.Length > 0)
                parts.Add(p);
        }
        return parts.Count == 0 ? "" : Capitalize(string.Join(", ", parts));
    }

    private static string OneWeatherPhrase(SnapshotWeather w, ReportVocabulary vocab)
    {
        if (w.Descriptor == WeatherDescriptor.Thunderstorm)
            return vocab.WxThunderstorm;

        if (w.Precipitation.Count > 0)
        {
            var word = PrecipWord(w.Precipitation[0], vocab);
            if (w.Descriptor == WeatherDescriptor.Freezing)
                return $"{vocab.WxFreezing} {Lower(word)}";
            if (w.Descriptor == WeatherDescriptor.Showers)
                return $"{vocab.WxShowers} {Lower(word)}";
            return w.Intensity switch
            {
                WeatherIntensity.Light => $"{vocab.WxLight} {Lower(word)}",
                WeatherIntensity.Heavy => $"{vocab.WxHeavy} {Lower(word)}",
                _ => word,
            };
        }

        return ObscurationWord(w.Obscuration, vocab) ?? "";
    }

    private static string ConditionsPhrase(DaySummary day, ReportVocabulary vocab)
    {
        if (day.Severe)
            return vocab.SevereStormsLead;

        var sky = day.Sky switch
        {
            SkyState.PartlyCloudy => vocab.SkyPartlyCloudy,
            SkyState.MostlyCloudy => vocab.SkyMostlyCloudy,
            SkyState.Overcast => vocab.SkyOvercast,
            _ => vocab.SkyClear,
        };

        if (day.PrecipExpectation == PrecipExpectation.None || day.Phenomenon is null)
            return day.Sky == SkyState.Clear ? $"{sky} {vocab.CondAndDry}" : sky;

        var precip = Lower(PhenomenonWord(day.Phenomenon.Value, vocab));
        var outlook = day.PrecipExpectation switch
        {
            PrecipExpectation.Possible => vocab.OutlookPossible,
            PrecipExpectation.Likely => vocab.OutlookLikely,
            _ => vocab.OutlookExpected,
        };
        var time = day.PrecipLocalHour switch
        {
            >= 6 and < 12 => vocab.CondMorning,
            >= 12 and < 18 => vocab.CondAfternoon,
            >= 18 => vocab.CondEvening,
            _ => vocab.CondOvernight,
        };
        return $"{sky}, {time} {precip} {outlook}";
    }

    private static string? ObscurationWord(WeatherObscuration? o, ReportVocabulary vocab) => o switch
    {
        WeatherObscuration.Fog => vocab.WxFog,
        WeatherObscuration.Mist => vocab.WxMist,
        WeatherObscuration.Haze => vocab.WxHaze,
        WeatherObscuration.Smoke => vocab.WxSmoke,
        _ => null,
    };

    private static string PrecipWord(PrecipitationType t, ReportVocabulary vocab) => t switch
    {
        PrecipitationType.Drizzle => vocab.WxDrizzle,
        PrecipitationType.Snow or PrecipitationType.SnowGrains => vocab.WxSnow,
        PrecipitationType.IcePellets or PrecipitationType.Hail or PrecipitationType.SmallHail => vocab.WxMixed,
        _ => vocab.WxRain,
    };

    private static string PhenomenonWord(PrecipPhenomenon p, ReportVocabulary vocab) => p switch
    {
        PrecipPhenomenon.Thunderstorm => vocab.Storms,
        PrecipPhenomenon.Snow => vocab.WxSnow,
        PrecipPhenomenon.Mixed => vocab.WxMixed,
        PrecipPhenomenon.FreezingPrecip => $"{vocab.WxFreezing} {Lower(vocab.WxRain)}",
        _ => vocab.WxRain,
    };

    // ── unit conversion (canonical → recipient unit + suffix) ─────────────────

    private static string RenderQuantity(QuantityKind kind, double value, Recipient r) => kind switch
    {
        QuantityKind.Temp => FormatTempC(value, r),
        QuantityKind.Wind or QuantityKind.Gust => FormatWindKt(value, r),
        QuantityKind.Pressure => FormatPressureHpa(value, r),
        QuantityKind.PrecipMm => FormatPrecipMm(value, r),
        _ => value.ToString(Inv),
    };

    private static string FormatTempC(double celsius, Recipient r) =>
        r.TempUnit == "C"
            ? $"{Math.Round(celsius).ToString("0", Inv)}°C"
            : $"{Math.Round(celsius * 9.0 / 5.0 + 32.0).ToString("0", Inv)}°F";

    private static string FormatWindKt(double kt, Recipient r) =>
        r.WindSpeedUnit == "kph"
            ? $"{Math.Round(kt * 1.852).ToString("0", Inv)} km/h"
            : $"{Math.Round(kt * 1.15078).ToString("0", Inv)} mph";

    private static string FormatPressureHpa(double hpa, Recipient r) =>
        r.PressureUnit == "kPa"
            ? $"{(hpa * 0.1).ToString("0.0", Inv)} kPa"
            : $"{(hpa / 33.8639).ToString("0.00", Inv)} inHg";

    private static string FormatPressureInHg(double inHg, Recipient r) =>
        r.PressureUnit == "kPa"
            ? $"{(inHg * 3.38639).ToString("0.0", Inv)} kPa"
            : $"{inHg.ToString("0.00", Inv)} inHg";

    private static string FormatPrecipMm(double mm, Recipient r) =>
        r.PrecipUnit == "mm"
            ? $"{mm.ToString("0.#", Inv)} mm"
            : $"{(mm / 25.4).ToString("0.00", Inv)} in";

    private static string RenderInstant(DateTime utc, TimeZoneInfo tz, CultureInfo culture)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.ToString("h:mm tt", culture);
    }

    // ── small helpers ─────────────────────────────────────────────────────────

    private static string Lower(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>HTML-escapes the structural characters only, preserving Unicode (°, accents, em dashes) for the UTF-8 email body.</summary>
    private static string HtmlText(string s) =>
        s.Replace("&", "&amp;", StringComparison.Ordinal)
         .Replace("<", "&lt;", StringComparison.Ordinal)
         .Replace(">", "&gt;", StringComparison.Ordinal);
}