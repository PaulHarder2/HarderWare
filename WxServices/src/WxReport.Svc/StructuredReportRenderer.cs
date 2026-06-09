using System.Globalization;
using System.Text;

using MetarParser.Data.Entities;

using WxInterp;

using WxServices.Common;
using WxServices.Logging;

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

    /// <summary>
    /// WX-148 tier-aware degrade: renders a narrative-less hazard report when
    /// reconciliation could not produce a self-consistent narrative but the
    /// snapshot parsed cleanly, and the forecast is safety-critical. A
    /// deterministic hazard banner leads (so the hazard can't be missed without
    /// the prose), then the normal current-conditions table and day-grid built
    /// from <paramref name="finalSnapshot"/>. There is NO "What's changed" band
    /// and NO closing — a summary we cannot deliver is left out, not apologized
    /// for. Always an unscheduled (alert) send.
    /// </summary>
    public static string RenderDegraded(
        ForecastSnapshotBody finalSnapshot,
        WeatherSnapshot observation,
        Recipient recipient,
        TimeZoneInfo localityTz,
        DateTime nowUtc)
    {
        var lang = LanguageHelper.ToIetfTag(recipient.Language).Split('-')[0].ToLowerInvariant();
        var vocab = ReportVocabulary.ForLanguage(lang);

        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");
        AppendHeader(sb, observation, localityTz, vocab, isUnscheduled: true);
        AppendHazardBanner(sb, finalSnapshot, localityTz, vocab, nowUtc);
        AppendCurrentConditions(sb, observation, recipient, vocab);
        AppendExtendedForecast(sb, finalSnapshot, observation.LocalityName, recipient, localityTz, vocab);
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>
    /// Deterministic hazard banner for a degraded safety-critical send: leads with
    /// the severe phenomenon and its local day-part, drawn from the earliest severe
    /// block that hasn't fully passed (<c>StartUtc + 6h &gt; nowUtc</c>) — matching the
    /// worker's degrade gate, so the banner names the same hazard that justified the
    /// send rather than a stale or later block. Emits nothing when no such block
    /// exists (the caller only degrades-and-sends when one does, so this is a
    /// defensive guard).
    /// </summary>
    private static void AppendHazardBanner(StringBuilder sb, ForecastSnapshotBody body, TimeZoneInfo tz, ReportVocabulary vocab, DateTime nowUtc)
    {
        var severe = body.Blocks
            .Where(b => b.SevereFlag && DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc).AddHours(6) > nowUtc)
            .OrderBy(b => b.StartUtc)
            .FirstOrDefault();
        if (severe is null)
            return;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(severe.StartUtc, DateTimeKind.Utc), tz);
        var timing = $"{local.ToString("dddd", vocab.Culture)} {Lower(PartLabel(PartOf(local.Hour), vocab))}";
        var text = string.Format(vocab.Culture, vocab.HazardBannerFormat, SevereNoun(severe.PrecipPhenomenon, vocab), timing);
        sb.Append("<div style=\"background:#7a1c1c;color:#ffffff;padding:14px 24px;font-weight:bold;font-size:15px;\">");
        sb.Append(HtmlText(text));
        sb.Append("</div>");
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
            var condHtml = ConditionsCellHtml(day, vocab);
            sb.Append($"<tr style=\"background:{bg};\">");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(date)}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{temps}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(wind)}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{condHtml}</td></tr>");
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
        IReadOnlyList<Episode> Episodes,
        bool Severe,
        int SevereLocalHour);

    /// <summary>
    /// One precipitation episode within a day: a maximal run of consecutive
    /// 6-hour blocks sharing a phenomenon.  <see cref="StartHour"/> / <see cref="EndHour"/>
    /// are the local hours of the run's first and last blocks, so an episode that
    /// crosses a time-of-day boundary renders as a range ("Overnight–morning").
    /// <see cref="Expectation"/> is the peak across the run; <see cref="Severe"/> is
    /// the logical OR of its blocks' severe flags.
    /// </summary>
    private sealed record Episode(
        int StartHour,
        int EndHour,
        PrecipExpectation Expectation,
        PrecipPhenomenon Phenomenon,
        bool Severe);

    private enum DayPart { Overnight, Morning, Afternoon, Evening }

    /// <summary>
    /// Buckets the snapshot's 6-hour blocks into per-local-calendar-day summaries:
    /// a day's high/low are the max/min across its blocks, wind the day's peak
    /// sustained, sky the most cloud-significant state, and precipitation is split
    /// into <see cref="Episode"/>s (a run of consecutive blocks sharing a phenomenon)
    /// rather than collapsed to a single peak — so a day with, say, morning rain and
    /// afternoon storms surfaces both instead of dropping one (WX-148, Class 2).
    /// Days are returned in chronological order; the first is today.
    /// </summary>
    private static IEnumerable<DaySummary> AggregateDays(ForecastSnapshotBody body, TimeZoneInfo tz)
    {
        var byDay = new Dictionary<DateOnly, List<(int Hour, ForecastSnapshotBlock Block)>>();
        var order = new List<DateOnly>();

        // Sort by StartUtc rather than trust body order: episode runs and the
        // earliest-severe pick below are order-dependent, and final_snapshot is
        // Claude-emitted (the schema documents "earliest first" but doesn't enforce it).
        foreach (var b in body.Blocks.OrderBy(b => b.StartUtc))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz);
            var day = DateOnly.FromDateTime(local);
            if (!byDay.TryGetValue(day, out var list))
            {
                list = [];
                byDay[day] = list;
                order.Add(day);
            }
            list.Add((local.Hour, b));
        }

        foreach (var day in order)
        {
            var blocks = byDay[day];  // sorted by StartUtc above
            // Day-level severe is the OR of every block's flag — independent of precip,
            // so a severe block with no precipitation (e.g. a damaging-wind event) still
            // marks the day severe and is timed by the earliest such block.
            var severeBlocks = blocks.Where(x => x.Block.SevereFlag).ToList();
            yield return new DaySummary(
                day,
                blocks.Max(x => x.Block.TemperatureCelsius.Max),
                blocks.Min(x => x.Block.TemperatureCelsius.Min),
                blocks.Max(x => x.Block.WindKt.Max),
                blocks.Select(x => x.Block.SkyState).Max(),  // SkyState ordinal increases with cloudiness
                BuildEpisodes(blocks),
                severeBlocks.Count > 0,
                severeBlocks.Count > 0 ? severeBlocks[0].Hour : 0);
        }
    }

    /// <summary>
    /// Splits a day's chronological blocks into precipitation episodes.  An episode
    /// is a maximal run of consecutive blocks sharing a phenomenon; a dry block, or
    /// a change of phenomenon, closes the current episode (and a phenomenon change
    /// opens a new one).  Each episode carries the local hours of its first and last
    /// blocks, its peak expectation, and a severe flag OR'd across the run.
    /// </summary>
    private static IReadOnlyList<Episode> BuildEpisodes(List<(int Hour, ForecastSnapshotBlock Block)> blocks)
    {
        var episodes = new List<Episode>();
        int startHour = 0, endHour = 0;
        PrecipPhenomenon phenomenon = default;
        PrecipExpectation peak = PrecipExpectation.None;
        DateTime prevStartUtc = default;
        bool severe = false, open = false;

        void Close()
        {
            if (open)
            {
                episodes.Add(new Episode(startHour, endHour, peak, phenomenon, severe));
                open = false;
            }
        }

        foreach (var (hour, b) in blocks)
        {
            if (b.PrecipExpectation == PrecipExpectation.None || b.PrecipPhenomenon is not PrecipPhenomenon p)
            {
                Close();
                continue;
            }

            // Extend only when this block is contiguous with the run: same phenomenon AND
            // immediately after the previous block (StartUtc == prev + 6h). A missing block
            // (a time gap) breaks the run even when the phenomenon matches, so two rain
            // spells either side of a dropped slot don't merge into one mistimed episode.
            if (open && p == phenomenon && b.StartUtc == prevStartUtc.AddHours(6))
            {
                endHour = hour;
                if ((int)b.PrecipExpectation > (int)peak)
                    peak = b.PrecipExpectation;
                severe |= b.SevereFlag;
            }
            else
            {
                Close();
                open = true;
                startHour = endHour = hour;
                phenomenon = p;
                peak = b.PrecipExpectation;
                severe = b.SevereFlag;
            }
            prevStartUtc = b.StartUtc;
        }

        Close();
        return episodes;
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

    /// <summary>
    /// Renders the per-day Conditions cell as an HTML fragment (each text piece
    /// already escaped).  Three shapes (WX-148, Class 2):
    /// <list type="bullet">
    /// <item>Severe day → a single sentence (deliberately breaks the tabular rhythm
    /// so the hazard stands out): clauses in chronological order joined by "then",
    /// the severe one rendered with its lead phrase. Checked first, so a severe block
    /// carrying no precipitation (a damaging-wind event, which forms no episode) still
    /// surfaces — led generically and timed by the earliest severe block.</item>
    /// <item>Dry day → the sky word, plus "and dry" when clear.</item>
    /// <item>Otherwise → one labeled line per episode ("Afternoon — rain likely"),
    /// chronological, an episode that crosses a time-of-day boundary timed as a
    /// range ("Overnight–morning").</item>
    /// </list>
    /// At most two episodes are shown; on a busier day the rest ride the narrative.
    /// </summary>
    private static string ConditionsCellHtml(DaySummary day, ReportVocabulary vocab)
    {
        var shown = SelectEpisodes(day);

        // Severe first — before the dry/empty check — so a severe block with no precip
        // (no episode) is never dropped to a benign sky phrase.
        if (day.Severe)
        {
            var clauses = new List<string>();
            bool severeNamed = false;
            foreach (var e in shown)
            {
                if (e.Severe)
                {
                    severeNamed = true;
                    clauses.Add($"{SevereNoun(e.Phenomenon, vocab)} {OutlookWord(e.Expectation, vocab)} {WhenWord(PartOf(e.StartHour), vocab)}");
                }
                else
                {
                    clauses.Add($"{Lower(PhenomenonWord(e.Phenomenon, vocab))} {WhenWord(PartOf(e.StartHour), vocab)}");
                }
            }
            // Severe but no severe precip episode → a non-convective hazard (e.g. damaging
            // wind); lead generically, timed by the earliest severe block, with any precip
            // episodes following.
            if (!severeNamed)
                clauses.Insert(0, $"{vocab.CondSevereWeather} {vocab.OutlookLikely} {WhenWord(PartOf(day.SevereLocalHour), vocab)}");
            return HtmlText(string.Join($", {vocab.CondThen} ", clauses));
        }

        if (shown.Count == 0)
        {
            var skyWord = SkyWord(day.Sky, vocab);
            return HtmlText(day.Sky == SkyState.Clear ? $"{skyWord} {vocab.CondAndDry}" : skyWord);
        }

        var lines = shown.Select(e =>
            HtmlText($"{EpisodeRangeLabel(e, vocab)} — {Lower(PhenomenonWord(e.Phenomenon, vocab))} {OutlookWord(e.Expectation, vocab)}"));
        return string.Join("<br/>", lines);
    }

    /// <summary>
    /// Returns the day's episodes to show, capped at two.  When a day has more, the
    /// two most significant — severe first (a severe episode must never be evicted by
    /// the cap), then expectation, then earliest — are kept and re-ordered
    /// chronologically; the drop is logged so the truncation is never silent (the
    /// narrative still carries the omitted episode).
    /// </summary>
    private static IReadOnlyList<Episode> SelectEpisodes(DaySummary day)
    {
        if (day.Episodes.Count <= 2)
            return day.Episodes;

        var top = day.Episodes
            .Select((e, i) => (e, i))
            .OrderByDescending(t => t.e.Severe)
            .ThenByDescending(t => (int)t.e.Expectation)
            .ThenBy(t => t.i)
            .Take(2)
            .OrderBy(t => t.i)
            .Select(t => t.e)
            .ToList();
        Logger.Warn($"Day {day.Date:yyyy-MM-dd} has {day.Episodes.Count} precip episodes; conditions grid shows 2, the narrative carries the rest.");
        return top;
    }

    private static DayPart PartOf(int hour) => hour switch
    {
        >= 6 and < 12 => DayPart.Morning,
        >= 12 and < 18 => DayPart.Afternoon,
        >= 18 => DayPart.Evening,
        _ => DayPart.Overnight,
    };

    private static string PartLabel(DayPart p, ReportVocabulary vocab) => p switch
    {
        DayPart.Morning => vocab.PartMorning,
        DayPart.Afternoon => vocab.PartAfternoon,
        DayPart.Evening => vocab.PartEvening,
        _ => vocab.PartOvernight,
    };

    private static string WhenWord(DayPart p, ReportVocabulary vocab) => p switch
    {
        DayPart.Morning => vocab.CondMorning,
        DayPart.Afternoon => vocab.CondAfternoon,
        DayPart.Evening => vocab.CondEvening,
        _ => vocab.CondOvernight,
    };

    private static string OutlookWord(PrecipExpectation e, ReportVocabulary vocab) => e switch
    {
        PrecipExpectation.Possible => vocab.OutlookPossible,
        PrecipExpectation.Likely => vocab.OutlookLikely,
        _ => vocab.OutlookExpected,
    };

    private static string SkyWord(SkyState s, ReportVocabulary vocab) => s switch
    {
        SkyState.PartlyCloudy => vocab.SkyPartlyCloudy,
        SkyState.MostlyCloudy => vocab.SkyMostlyCloudy,
        SkyState.Overcast => vocab.SkyOvercast,
        _ => vocab.SkyClear,
    };

    /// <summary>The severe lead noun: convective ("Severe storms") for a thunderstorm, generic ("Severe weather") otherwise — a severe block can be a damaging-wind event with no precip.</summary>
    private static string SevereNoun(PrecipPhenomenon? phenomenon, ReportVocabulary vocab) =>
        phenomenon == PrecipPhenomenon.Thunderstorm ? vocab.CondSevereStorms : vocab.CondSevereWeather;

    /// <summary>The episode's time label: a single day-part, or a "start–end" range when it spans buckets.</summary>
    private static string EpisodeRangeLabel(Episode e, ReportVocabulary vocab)
    {
        var start = PartOf(e.StartHour);
        var end = PartOf(e.EndHour);
        return start == end
            ? PartLabel(start, vocab)
            : $"{PartLabel(start, vocab)}–{Lower(PartLabel(end, vocab))}";
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