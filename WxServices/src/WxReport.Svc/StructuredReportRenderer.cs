using System.Globalization;
using System.Text;

using MetarParser.Data.Entities;

using WxInterp;

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
    /// rendering.  <paramref name="kind"/> selects the header's report-type label
    /// (always shown); the change-summary band itself renders whenever the
    /// narrative carries one — independent of the kind.  (A recipient's first contact is a separate
    /// welcome-only email — see <see cref="RenderWelcome"/>; this method always
    /// renders a weather report.)  <paramref name="nowUtc"/> is the send instant: the
    /// Extended Forecast grid drops any local day whose every 6-hour block has fully
    /// elapsed by then (WX-188), so it never leads with a wholly-past day.
    /// </summary>
    public static string Render(
        StructuredReportBody report,
        ForecastSnapshotBody finalSnapshot,
        WeatherSnapshot observation,
        Recipient recipient,
        string langCode,
        TimeZoneInfo localityTz,
        ReportKind kind,
        DateTime nowUtc)
    {
        // langCode is the recipient's resolved ISO 639-1 code (en, es), resolved by
        // the caller from the Languages registry (WX-166). The narrative map and
        // ReportVocabulary key on it; normalize defensively so a future regional tag
        // (e.g. "es-419") can't silently miss the narrative and fall back.
        var lang = NormalizeLang(langCode);
        var sections = SelectNarrative(report, lang);
        var vocab = ReportVocabulary.ForLanguage(lang);

        string RenderProse(string prose) => HtmlText(ReportTokens.Substitute(
            prose,
            (kind, value) => RenderQuantity(kind, value, recipient),
            instant => RenderInstant(instant, localityTz, vocab.Culture)));

        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");

        AppendHeader(sb, observation, localityTz, vocab, kind);

        // WX-189: the band shows whenever one is warranted — Claude's changeSummary
        // prose when present, else a deterministic line from the computed changes.
        // Band PRESENCE is already decided upstream: ReportWorker strips the band
        // (changeSummary + changes both cleared) on a scheduled report with no
        // near-term severe onset (WX-178), and never strips an unscheduled one. So a
        // non-empty Changes here means the band must show; the fallback covers the
        // case where the model's prose was rejected or absent.
        if (!string.IsNullOrWhiteSpace(sections.ChangeSummary))
            AppendChangeBand(sb, vocab, RenderProse(sections.ChangeSummary!));
        else if (report.Changes.Count > 0)
            AppendChangeBand(sb, vocab, HtmlText(RenderFallbackBand(report.Changes, vocab, localityTz)));

        AppendCurrentConditions(sb, observation, recipient, vocab);
        AppendExtendedForecast(sb, finalSnapshot, observation.LocalityName, recipient, localityTz, vocab, nowUtc);
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
    /// for. Always an unscheduled send.
    /// </summary>
    public static string RenderDegraded(
        ForecastSnapshotBody finalSnapshot,
        WeatherSnapshot observation,
        Recipient recipient,
        string langCode,
        TimeZoneInfo localityTz,
        DateTime nowUtc)
    {
        var lang = NormalizeLang(langCode);
        var vocab = ReportVocabulary.ForLanguage(lang);

        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");
        AppendHeader(sb, observation, localityTz, vocab, ReportKind.Unscheduled);
        AppendHazardBanner(sb, finalSnapshot, localityTz, vocab, nowUtc);
        AppendCurrentConditions(sb, observation, recipient, vocab);
        AppendExtendedForecast(sb, finalSnapshot, observation.LocalityName, recipient, localityTz, vocab, nowUtc);
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
        var severe = SevereBlocks.EarliestActive(body, nowUtc);
        if (severe is null)
            return;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(severe.StartUtc, DateTimeKind.Utc), tz);
        var timing = ProseTiming(local, vocab);
        var text = string.Format(vocab.Culture, vocab.HazardBannerFormat, vocab.SevereNoun(severe.PrecipPhenomenon), timing);
        sb.Append("<div style=\"background:#7a1c1c;color:#ffffff;padding:14px 24px;font-weight:bold;font-size:15px;\">");
        sb.Append(HtmlText(text));
        sb.Append("</div>");
    }

    // ── WX-189 deterministic change-band fallback ─────────────────────────────

    /// <summary>
    /// Deterministic "What's changed" band, reached only when a band is warranted
    /// (an unscheduled report, or a scheduled one with a near-term severe onset) but
    /// Claude's changeSummary prose was rejected or absent. Renders the top one or two
    /// already-salience-ranked changes as terse, label-style localized lines
    /// ("Severe storms — Saturday afternoon"). Plain and correct by design — this path
    /// is hit only when the model's own prose failed — and always names the calendar
    /// day explicitly (never a bare "overnight"; see WX-190).
    /// </summary>
    private static string RenderFallbackBand(
        IReadOnlyList<ReportChange> changes, ReportVocabulary vocab, TimeZoneInfo tz)
    {
        var lines = changes.Take(2).Select(c =>
        {
            var dir = DirectionWord(c, vocab);
            var timing = ChangeTiming(c.Window.StartUtc, vocab, tz);
            return dir.Length == 0
                ? $"{ChangeNoun(c, vocab)} — {timing}."
                : $"{ChangeNoun(c, vocab)} {dir} — {timing}.";
        });
        return string.Join(" ", lines);
    }

    // Localized direction gerund for a precip/severe change ("Rain easing", "Snow ending")
    // — without it a clearing/weakening change reads as arriving. Temperature and wind
    // nouns are already direction-neutral ("Temperature change"), so they take no word;
    // Shifting (WX-191) has none yet.
    private static string DirectionWord(ReportChange c, ReportVocabulary vocab) =>
        c.Phenomenon is ChangePhenomenon.Temperature or ChangePhenomenon.Wind or ChangePhenomenon.WindShift
            ? string.Empty
            : c.Direction switch
            {
                ChangeDirection.Appearing => vocab.DirAppearing,
                ChangeDirection.Strengthening => vocab.DirStrengthening,
                ChangeDirection.Weakening => vocab.DirWeakening,
                ChangeDirection.Clearing => vocab.DirClearing,
                _ => string.Empty,
            };

    // Localized "{weekday} {day-part}" for a change window's start ("Saturday afternoon"),
    // mirroring AppendHazardBanner so the fallback reads like the rest of the report.
    private static string ChangeTiming(DateTime startUtc, ReportVocabulary vocab, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz);
        return ProseTiming(local, vocab);
    }

    // Localized, capitalized noun phrase for a computed change. Precipitation and severe
    // reuse the grid's weather vocabulary — a Safety-tier convective change uses the
    // severe lead, matching the hazard banner and the WX-156 subject — while freezing
    // precip, temperature, and wind use their own brief nouns (WX-191's WindShift never
    // reaches here yet).
    private static string ChangeNoun(ReportChange c, ReportVocabulary vocab) => Capitalize(c.Phenomenon switch
    {
        ChangePhenomenon.Rain => vocab.WxRain,
        ChangePhenomenon.Thunderstorm => c.Tier == ChangeTier.Safety ? vocab.CondSevereStorms : vocab.WxThunderstorm,
        ChangePhenomenon.Snow => vocab.WxSnow,
        ChangePhenomenon.Mixed => vocab.WxMixed,
        ChangePhenomenon.FreezingPrecip => vocab.ChangeFreezingRain,
        ChangePhenomenon.Severe => vocab.CondSevereWeather,
        ChangePhenomenon.Temperature => vocab.ChangeTempNoun,
        ChangePhenomenon.Wind => vocab.ChangeWindNoun,
        _ => vocab.ChangeWindNoun,
    });

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
        string langCode,
        string localityName,
        TimeZoneInfo localityTz,
        IReadOnlyList<int> scheduledHours)
    {
        var lang = NormalizeLang(langCode);
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
    public static string WelcomePlainText(Recipient recipient, string langCode, string localityName, IReadOnlyList<int> scheduledHours)
    {
        var lang = NormalizeLang(langCode);
        var vocab = ReportVocabulary.ForLanguage(lang);
        return $"{vocab.WelcomeGreeting} "
            + string.Format(vocab.Culture, vocab.WelcomeFormat, localityName, FormatScheduleTimes(scheduledHours, vocab));
    }

    /// <summary>
    /// Normalizes a resolved language code to the bare lower-case ISO 639-1 part the
    /// narrative map and <see cref="ReportVocabulary"/> key on — dropping any regional
    /// suffix (e.g. <c>"es-419"</c> → <c>"es"</c>) so a future regional tag can't
    /// silently miss the narrative and fall back. Single source so the render entry
    /// points can't drift.
    /// </summary>
    private static string NormalizeLang(string langCode) => langCode.Split('-')[0].ToLowerInvariant();

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
        StringBuilder sb, WeatherSnapshot snap, TimeZoneInfo tz, ReportVocabulary vocab, ReportKind kind)
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
        var typeLabel = vocab.GetFromReportKind(kind, LabelType.Header);
        sb.Append($"<div style=\"font-style:italic;font-size:13px;color:#a0bcd4;\">{HtmlText(typeLabel)}</div>");
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
        StringBuilder sb, ForecastSnapshotBody body, string locationName, Recipient recipient, TimeZoneInfo tz, ReportVocabulary vocab, DateTime nowUtc)
    {
        var days = AggregateDays(body, tz, nowUtc).ToList();
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
        sb.Append("</table>");
        // 24-hour clock legend (WX-190), directly beneath the grid where a reader meets
        // an unfamiliar band label: the Conditions cell tiles each day into XX-YY clock
        // bands, so the legend sits one glance away rather than in a distant footer. Styled
        // to match the meteogram caption (WX-195) — centered, italic, 11px, #888 — so the
        // report's two explanatory captions read as one consistent voice.
        sb.Append($"<div style=\"text-align:center;font-size:11px;color:#888;font-style:italic;margin-top:6px;\">{HtmlText(vocab.GridTimeLegend)}</div>");
        sb.Append("</div>");
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

    /// <summary>
    /// One local calendar day's forecast.  High/low/wind span the whole day (computed from ALL
    /// blocks); <see cref="Bands"/> carries only the day's still-live 6-hour blocks in chronological
    /// order (one per local day-part, WX-155; fully-elapsed blocks dropped per WX-195), from which
    /// the Conditions cell tiles the remaining clock bands (WX-190).
    /// </summary>
    private sealed record DaySummary(
        DateOnly Date,
        double MaxTempC,
        double MinTempC,
        int MaxWindKt,
        IReadOnlyList<(int Hour, ForecastSnapshotBlock Block)> Bands);

    private enum DayPart { Overnight, Morning, Afternoon, Evening }

    /// <summary>
    /// Buckets the snapshot's 6-hour blocks into per-local-calendar-day summaries:
    /// a day's high/low are the max/min across its blocks, wind the day's peak
    /// sustained, and the day's blocks are carried through whole as <c>Bands</c> for the
    /// Conditions cell to tile into clock bands — so a day with, say, morning rain and
    /// afternoon storms surfaces both, each in its own band, instead of dropping one
    /// (WX-148, Class 2; WX-190).
    /// Days whose every block has fully elapsed at <paramref name="nowUtc"/> are dropped
    /// (WX-188), so the first day returned is the one containing the send instant, never a
    /// wholly-past "yesterday".  A retained day's high/low/wind are computed from ALL its blocks
    /// — including any already elapsed — so they span the whole calendar day regardless of how
    /// much of the day remains (a 1 PM report still reports this morning's low as today's low),
    /// but its <c>Bands</c> (the Conditions tiling) carry only the still-live blocks, so today
    /// leads with the current day-part, not one already past (WX-195).
    /// Days are returned in chronological order.
    /// </summary>
    private static IEnumerable<DaySummary> AggregateDays(ForecastSnapshotBody body, TimeZoneInfo tz, DateTime nowUtc)
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
            var blocks = byDay[day];  // all of the day's blocks, sorted by StartUtc above

            // WX-195: the Conditions cell tiles only the day's still-relevant bands — drop any
            // band that has fully elapsed at nowUtc, so today's row leads with the CURRENT
            // day-part rather than a period already past. A day with no live band left (a
            // wholly-past "yesterday") is dropped entirely (WX-188).
            var liveBands = blocks.Where(x => SevereBlocks.NotFullyElapsed(x.Block, nowUtc)).ToList();
            if (liveBands.Count == 0)
                continue;
            // WX-176/WX-188: high/low/peak wind span the WHOLE calendar day — computed from ALL
            // blocks, including any already elapsed — so a midday report still reports this
            // morning's low as today's low. Only the Conditions tiling (liveBands) is trimmed.
            yield return new DaySummary(
                day,
                blocks.Max(x => x.Block.TemperatureCelsius.Max),
                blocks.Min(x => x.Block.TemperatureCelsius.Min),
                blocks.Max(x => x.Block.WindKt.Max),
                liveBands);
        }
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
    /// Renders the per-day Conditions cell as an HTML fragment (each text piece already
    /// escaped).  The day is tiled into its 6-hour clock bands (00-06 / 06-12 / 12-18 /
    /// 18-24 — the snapshot's native blocks); each band contributes one condition phrase,
    /// and adjacent bands sharing a phrase merge into a single labeled line.  So when the
    /// day's four 6-hour blocks are present the cell tiles the whole day with no gaps (WX-190):
    /// a uniform day collapses to one "00-24 — …" line, a mixed day shows two to four
    /// "XX-YY — …" lines in clock order.  A missing interior block leaves a visible gap between
    /// bands (bands merge only when clock-contiguous) rather than a fabricated span over hours
    /// the snapshot does not carry.  A severe band is emphasized (bold); its clock range plus
    /// the grid row's date bind the hazard to the correct calendar day, so no floating
    /// "overnight" is ever needed.
    /// </summary>
    private static string ConditionsCellHtml(DaySummary day, ReportVocabulary vocab)
    {
        if (day.Bands.Count == 0)
            return "—";  // defensive: AggregateDays only yields days with at least one block

        // Collapse consecutive bands with an identical phrase into runs, tracking each run's
        // first and last band-start hours so its label spans the whole run. The merge requires
        // clock-contiguity (this band starts 6h after the run's last) as well as an identical
        // phrase: a missing interior block must NOT be bridged, or the merged label would
        // assert coverage of hours the snapshot has no block for.
        var runs = new List<(int FirstStart, int LastStart, string Phrase, bool Severe)>();
        foreach (var (hour, block) in day.Bands)
        {
            var (phrase, severe) = BandPhrase(block, vocab);
            if (runs.Count > 0 && runs[^1].Phrase == phrase && runs[^1].Severe == severe
                && hour == runs[^1].LastStart + 6)
                runs[^1] = (runs[^1].FirstStart, hour, phrase, severe);
            else
                runs.Add((hour, hour, phrase, severe));
        }

        var lines = runs.Select(r =>
        {
            var line = HtmlText($"{ClockBandSpan(r.FirstStart, r.LastStart)} — {r.Phrase}");
            return r.Severe ? $"<strong>{line}</strong>" : line;
        });
        return string.Join("<br/>", lines);
    }

    /// <summary>
    /// The condition phrase for one clock band's block, with a flag marking a severe band
    /// (which the caller emphasizes).  Severe leads with its noun ("Severe storms" /
    /// "Severe weather"); otherwise a precipitation band reads "{phenomenon} {outlook}"
    /// and a dry band reads its sky word ("Clear and dry" when clear).  Capitalized so
    /// every tiled line in the cell opens with a consistent leading capital.
    /// </summary>
    private static (string Phrase, bool Severe) BandPhrase(ForecastSnapshotBlock block, ReportVocabulary vocab)
    {
        if (block.SevereFlag)
        {
            // A severe block may carry no precip (a damaging-wind event); SevereNoun(null)
            // gives the generic lead, and a missing expectation falls back to "likely".
            var outlook = block.PrecipExpectation == PrecipExpectation.None
                ? vocab.OutlookLikely
                : OutlookWord(block.PrecipExpectation, vocab);
            return ($"{vocab.SevereNoun(block.PrecipPhenomenon)} {outlook}", true);
        }

        if (block.PrecipExpectation != PrecipExpectation.None && block.PrecipPhenomenon is PrecipPhenomenon p)
            return (Capitalize($"{PhenomenonWord(p, vocab)} {OutlookWord(block.PrecipExpectation, vocab)}"), false);

        var sky = SkyWord(block.SkyState, vocab);
        return (block.SkyState == SkyState.Clear ? $"{sky} {vocab.CondAndDry}" : sky, false);
    }

    private static DayPart PartOf(int hour) => hour switch
    {
        >= 6 and < 12 => DayPart.Morning,
        >= 12 and < 18 => DayPart.Afternoon,
        >= 18 => DayPart.Evening,
        _ => DayPart.Overnight,
    };

    /// <summary>
    /// "{weekday} {day-part}" for a prose instant — used by the hazard banner and the
    /// WX-189 fallback band, neither of which sits inside a day-bound grid row.  The
    /// day-owned parts keep idiom ("Saturday afternoon"); the 00-06 pre-dawn block is
    /// bound by its clock range ("Saturday 00-06") rather than a floating night-word,
    /// which a US reader would otherwise read as the following calendar day (WX-190).
    /// </summary>
    private static string ProseTiming(DateTime local, ReportVocabulary vocab) =>
        $"{local.ToString("dddd", vocab.Culture)} {ProsePart(local.Hour, vocab)}";

    private static string ProsePart(int localHour, ReportVocabulary vocab) => PartOf(localHour) switch
    {
        DayPart.Morning => Lower(vocab.PartMorning),
        DayPart.Afternoon => Lower(vocab.PartAfternoon),
        DayPart.Evening => Lower(vocab.PartEvening),
        _ => ClockBand(localHour),
    };

    /// <summary>The 24-hour "XX-YY" label of the single 6-hour band containing <paramref name="localHour"/> ("00-06" … "18-24").</summary>
    private static string ClockBand(int localHour)
    {
        int start = localHour / 6 * 6;
        return ClockBandSpan(start, start);
    }

    /// <summary>The 24-hour "XX-YY" label spanning a merged run of bands, from band-start hour <paramref name="firstStart"/> through the band starting at <paramref name="lastStart"/> ("00-24" for a whole day).</summary>
    private static string ClockBandSpan(int firstStart, int lastStart) =>
        $"{firstStart:00}-{lastStart + 6:00}";

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