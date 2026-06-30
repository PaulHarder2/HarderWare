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
/// Localized words come from the DB-backed <see cref="TemplateSet"/> the caller
/// resolves per language (WX-171): the renderer never glues two vocabulary items,
/// it looks up the grammar-sensitive combination as a single atomic
/// <see cref="Tok"/> token — translated as a unit — and only ever interpolates
/// runtime data (numbers, names, dates) into a translated format string. A missing
/// token throws (fail-closed); completeness is verified before a recipient is
/// rendered, so a miss here is a defect, not a degrade.
/// </para>
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
    /// rendering.  <paramref name="templates"/> supplies the language's localized
    /// phrases (resolved by the caller from the DB template store) and
    /// <paramref name="culture"/> its date/number locale.  <paramref name="kind"/>
    /// selects the header's report-type label (always shown); the change-summary
    /// band itself renders whenever the narrative carries one — independent of the
    /// kind.  (A recipient's first contact is a separate welcome-only email — see
    /// <see cref="RenderWelcome"/>; this method always renders a weather report.)
    /// <paramref name="nowUtc"/> is the send instant: the Extended Forecast grid
    /// drops any local day whose every 6-hour block has fully elapsed by then
    /// (WX-188), so it never leads with a wholly-past day.
    /// </summary>
    public static string Render(
        StructuredReportBody report,
        ForecastSnapshotBody finalSnapshot,
        WeatherSnapshot observation,
        Recipient recipient,
        TemplateSet templates,
        CultureInfo culture,
        TimeZoneInfo localityTz,
        ReportKind kind,
        DateTime nowUtc)
    {
        // templates.Iso is the recipient's resolved bare ISO 639-1 code (en, es), keyed by
        // the caller from the Languages registry (WX-166); the narrative map keys on the same.
        var sections = SelectNarrative(report, templates.Iso);

        string RenderProse(string prose) => HtmlText(ReportTokens.Substitute(
            prose,
            (kind, value) => RenderQuantity(kind, value, recipient),
            instant => RenderInstant(instant, localityTz, culture),
            (loC, hiC) => FormatTempRangeC(loC, hiC, recipient)));

        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");

        AppendHeader(sb, observation, templates, kind);

        // WX-189: the band shows whenever one is warranted — Claude's changeSummary
        // prose when present, else a deterministic line from the computed changes.
        // Band PRESENCE is already decided upstream: ReportWorker strips the band
        // (changeSummary + changes both cleared) on a scheduled report with no
        // near-term severe onset (WX-178), and never strips an unscheduled one. So a
        // non-empty Changes here means the band must show; the fallback covers the
        // case where the model's prose was rejected or absent.
        if (!string.IsNullOrWhiteSpace(sections.ChangeSummary))
            AppendChangeBand(sb, templates, RenderProse(sections.ChangeSummary!));
        else if (report.Changes.Count > 0)
            AppendChangeBand(sb, templates, HtmlText(RenderFallbackBand(report.Changes, templates, culture, localityTz)));

        AppendCurrentConditions(sb, observation, recipient, templates, localityTz, culture);
        AppendExtendedForecast(sb, finalSnapshot, observation.LocalityName, recipient, localityTz, templates, culture, nowUtc);
        AppendClosing(sb, templates, RenderProse(sections.Closing));

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
        TemplateSet templates,
        CultureInfo culture,
        TimeZoneInfo localityTz,
        DateTime nowUtc)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");
        AppendHeader(sb, observation, templates, ReportKind.Unscheduled);
        AppendHazardBanner(sb, finalSnapshot, localityTz, templates, culture, nowUtc);
        AppendCurrentConditions(sb, observation, recipient, templates, localityTz, culture);
        AppendExtendedForecast(sb, finalSnapshot, observation.LocalityName, recipient, localityTz, templates, culture, nowUtc);
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
    private static void AppendHazardBanner(StringBuilder sb, ForecastSnapshotBody body, TimeZoneInfo tz, TemplateSet t, CultureInfo culture, DateTime nowUtc)
    {
        var severe = SevereBlocks.EarliestActive(body, nowUtc);
        if (severe is null)
            return;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(severe.StartUtc, DateTimeKind.Utc), tz);
        var timing = ProseTiming(local, t, culture);
        var text = string.Format(culture, t.Get(Tok.HazardBannerFormat), t.Get(ReportLabels.SevereNounToken(severe.PrecipPhenomenon)), timing);
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
        IReadOnlyList<ReportChange> changes, TemplateSet t, CultureInfo culture, TimeZoneInfo tz)
    {
        var lines = changes.Take(2).Select(c =>
        {
            var dir = DirectionWord(c, t);
            var timing = ChangeTiming(c.Window.StartUtc, t, culture, tz);
            return dir.Length == 0
                ? $"{ChangeNoun(c, t)} — {timing}."
                : $"{ChangeNoun(c, t)} {dir} — {timing}.";
        });
        return string.Join(" ", lines);
    }

    // Localized direction gerund for a precip/severe change ("Rain easing", "Snow ending")
    // — without it a clearing/weakening change reads as arriving. Temperature and wind
    // nouns are already direction-neutral ("Temperature change"), so they take no word;
    // Shifting (WX-191) has none yet.
    private static string DirectionWord(ReportChange c, TemplateSet t) =>
        c.Phenomenon is ChangePhenomenon.Temperature or ChangePhenomenon.Wind or ChangePhenomenon.WindShift
            ? string.Empty
            : c.Direction switch
            {
                ChangeDirection.Appearing => t.Get(Tok.DirAppearing),
                ChangeDirection.Strengthening => t.Get(Tok.DirStrengthening),
                ChangeDirection.Weakening => t.Get(Tok.DirWeakening),
                ChangeDirection.Clearing => t.Get(Tok.DirClearing),
                _ => string.Empty,
            };

    // Localized "{weekday} {day-part}" for a change window's start ("Saturday afternoon"),
    // mirroring AppendHazardBanner so the fallback reads like the rest of the report.
    private static string ChangeTiming(DateTime startUtc, TemplateSet t, CultureInfo culture, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz);
        return ProseTiming(local, t, culture);
    }

    // Localized, capitalized noun phrase for a computed change. Precipitation and severe
    // reuse the grid's weather vocabulary — a Safety-tier convective change uses the
    // severe lead, matching the hazard banner and the WX-156 subject — while freezing
    // precip (the atomic rain_freezing token, correct order/agreement in every language),
    // temperature, and wind use their own brief nouns (WX-191's WindShift never reaches
    // here yet).
    private static string ChangeNoun(ReportChange c, TemplateSet t) => Capitalize(c.Phenomenon switch
    {
        ChangePhenomenon.Rain => t.Get(Tok.Rain),
        ChangePhenomenon.Thunderstorm => c.Tier == ChangeTier.Safety ? t.Get(Tok.CondSevereStorms) : t.Get(Tok.WxThunderstorm),
        ChangePhenomenon.Snow => t.Get(Tok.Snow),
        ChangePhenomenon.Mixed => t.Get(Tok.WintryMix),
        ChangePhenomenon.FreezingPrecip => t.Get(Tok.RainFreezing),
        ChangePhenomenon.Severe => t.Get(Tok.CondSevereWeather),
        ChangePhenomenon.Temperature => t.Get(Tok.ChangeTempNoun),
        ChangePhenomenon.Wind => t.Get(Tok.ChangeWindNoun),
        _ => t.Get(Tok.ChangeWindNoun),
    });

    // ── first-contact welcome (WX-130; standalone welcome-only email) ──────────

    /// <summary>
    /// Renders a recipient's one-time welcome email — a greeting plus a statement
    /// of what to expect (daily reports for <paramref name="localityName"/> at the
    /// locality's <paramref name="scheduledHours"/>, localized via
    /// <paramref name="templates"/> / <paramref name="culture"/>), with <b>no weather
    /// content</b>.  This is the first contact a new recipient receives; weather
    /// reports begin on the locality's normal cadence.
    /// </summary>
    public static string RenderWelcome(
        Recipient recipient,
        TemplateSet templates,
        CultureInfo culture,
        string localityName,
        TimeZoneInfo localityTz,
        IReadOnlyList<int> scheduledHours)
    {
        var body = string.Format(culture, templates.Get(Tok.WelcomeFormat), localityName, FormatScheduleTimes(scheduledHours, culture, templates));

        var sb = new StringBuilder();
        sb.Append("<div style=\"max-width:600px;margin:0 auto;font-family:Arial,Helvetica,sans-serif;color:#1a3a5c;\">");
        sb.Append("<div style=\"background:#1a3a5c;color:#ffffff;text-align:left;padding:20px 24px;border-radius:6px 6px 0 0;\">");
        sb.Append($"<div style=\"font-weight:bold;font-size:22px;\">{HtmlText(localityName)}</div>");
        sb.Append("</div>");
        sb.Append("<div style=\"background:#eef4fb;padding:20px 24px;font-size:15px;line-height:1.5;border-radius:0 0 6px 6px;\">");
        sb.Append($"<strong>{HtmlText(templates.Get(Tok.WelcomeGreeting))}</strong> {HtmlText(body)}");
        sb.Append("</div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>Plain-text form of the welcome email (the SMTP fallback), sharing the same templates + schedule formatting as <see cref="RenderWelcome"/> so the two cannot drift.</summary>
    public static string WelcomePlainText(Recipient recipient, TemplateSet templates, CultureInfo culture, string localityName, IReadOnlyList<int> scheduledHours)
    {
        return $"{templates.Get(Tok.WelcomeGreeting)} "
            + string.Format(culture, templates.Get(Tok.WelcomeFormat), localityName, FormatScheduleTimes(scheduledHours, culture, templates));
    }

    /// <summary>Localized "6 AM and 12 PM" list of the recipient's daily send hours, joined with the language's conjunction. Empty when no hours are configured.</summary>
    private static string FormatScheduleTimes(IReadOnlyList<int> hours, CultureInfo culture, TemplateSet t)
    {
        var times = hours
            .Select(h => new DateTime(2000, 1, 1, h, 0, 0).ToString("h tt", culture))
            .ToList();
        if (times.Count == 0)
            return "";
        if (times.Count == 1)
            return times[0];
        return string.Join(", ", times.Take(times.Count - 1)) + $" {t.Get(Tok.AndConjunction)} " + times[^1];
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
        StringBuilder sb, WeatherSnapshot snap, TemplateSet t, ReportKind kind)
    {
        // WX-184: the header carries the locality and the report-type label only. The
        // observation timestamp moved to the Current Conditions block (AppendCurrentConditions),
        // where, beneath the "Reported Conditions" heading, it unambiguously reads as the
        // observation time rather than an undated "what this is about" line — and is no
        // longer rendered twice.
        sb.Append("<div style=\"background:#1a3a5c;color:#ffffff;text-align:left;padding:20px 24px;border-radius:6px 6px 0 0;\">");
        sb.Append($"<div style=\"font-weight:bold;font-size:22px;\">{HtmlText(snap.LocalityName)}</div>");
        var typeLabel = t.Get(ReportLabels.TokenFor(kind, LabelType.Header));
        sb.Append($"<div style=\"font-style:italic;font-size:13px;color:#a0bcd4;\">{HtmlText(typeLabel)}</div>");
        sb.Append("</div>");
    }

    // ── change-summary band ──────────────────────────────────────────────────

    private static void AppendChangeBand(StringBuilder sb, TemplateSet t, string renderedProse)
    {
        sb.Append("<div style=\"background:#fef6e4;border-left:4px solid #e8a020;padding:14px 20px;font-size:14px;\">");
        sb.Append($"<strong>{HtmlText(t.Get(Tok.WhatsChangedLabel))}</strong> {renderedProse}");
        sb.Append("</div>");
    }

    // ── current conditions ────────────────────────────────────────────────────

    private static void AppendCurrentConditions(
        StringBuilder sb, WeatherSnapshot snap, Recipient recipient, TemplateSet t,
        TimeZoneInfo tz, CultureInfo culture)
    {
        sb.Append("<div style=\"background:#f7f9fc;padding:20px 24px;\">");
        AppendSectionHeading(sb, t.Get(Tok.CurrentConditionsHeading));

        // Station + observation-time attribution beneath the heading (WX-130 station,
        // WX-184 time): name the observing station when it differs from the locality,
        // and show WHEN the observation was taken — so a reader judges freshness here,
        // at the data, rather than inferring it from the header date. The time is the
        // absolute local instant (no relative "X ago"), which keeps this line free of
        // any new per-language vocabulary.
        var station = StationSubtitle(snap, t);
        string? obsWhen = null;
        if (snap.ObservationAvailable)
        {
            var obsLocal = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(snap.ObservationTimeUtc, DateTimeKind.Utc), tz);
            // Culture-aware short time ("t"), not a hardcoded "h:mm tt": 24-hour locales
            // (de, da) define empty AM/PM designators, so "h:mm tt" would render an
            // ambiguous 12-hour "6:00"; "t" honors each culture's clock convention.
            obsWhen = $"{obsLocal.ToString("ddd, MMM d", culture)}, {obsLocal.ToString("t", culture)}";
        }
        var attribution = (station, obsWhen) switch
        {
            (not null, not null) => $"{station} · {obsWhen}",
            (not null, null) => station,
            (null, not null) => obsWhen,
            _ => null,
        };
        if (attribution is not null)
            sb.Append($"<div style=\"font-size:13px;font-style:italic;color:#6b8fa8;font-weight:normal;margin-top:2px;\">{HtmlText(attribution)}</div>");

        if (!snap.ObservationAvailable)
        {
            var note = string.IsNullOrWhiteSpace(snap.ObservationUnavailableNote)
                ? t.Get(Tok.NoObservationNote)
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

        Row(t.Get(Tok.RowSky), SkyPhrase(snap.SkyLayers, t));
        Row(t.Get(Tok.RowVisibility), VisibilityPhrase(snap, t));
        Row(t.Get(Tok.RowWind), WindPhrase(snap, recipient, t));
        var weather = WeatherPhrase(snap.WeatherPhenomena, t);
        if (weather.Length > 0)
            Row(t.Get(Tok.RowWeather), weather);
        if (snap.TemperatureCelsius is double tc)
            Row(t.Get(Tok.RowTemperature), FormatTempC(tc, recipient));
        if (snap.TemperatureCelsius is double t2 && snap.DewPointCelsius is double dp)
            Row(t.Get(Tok.RowHumidity), $"{Meteorology.RelativeHumidity(t2, dp):0}%");
        if (snap.AltimeterInHg is double inHg)
            Row(t.Get(Tok.RowPressure), FormatPressureInHg(inHg, recipient));

        sb.Append("</table></div>");
    }

    /// <summary>
    /// The "at &lt;station&gt;" attribution shown under the Current Conditions
    /// heading, or <see langword="null"/> when the observing station IS the
    /// locality (no attribution needed) or no station metadata is available.
    /// Ported from the former reconciler-prompt subtitle logic (WX-130): prefer
    /// "at &lt;municipality&gt;, &lt;airport&gt;", collapse when the airport name
    /// already contains the municipality, fall back to whichever single name exists.
    /// The "at {0}" framing is the atomic <see cref="Tok.StationSubtitle"/> format
    /// string, so the preposition localizes as a unit ("en {0}").
    /// </summary>
    private static string? StationSubtitle(WeatherSnapshot snap, TemplateSet t)
    {
        var municipality = snap.StationMunicipality;
        var airportName = snap.StationName;
        var locality = snap.LocalityName;

        if (municipality is not null &&
            string.Equals(municipality, locality, StringComparison.OrdinalIgnoreCase))
            return null;

        string? name;
        if (municipality is not null && airportName is not null)
            name = airportName.Contains(municipality, StringComparison.OrdinalIgnoreCase)
                ? airportName
                : $"{municipality}, {airportName}";
        else
            name = airportName ?? municipality;

        return name is null ? null : string.Format(Inv, t.Get(Tok.StationSubtitle), name);
    }

    // ── extended forecast grid ────────────────────────────────────────────────

    private static void AppendExtendedForecast(
        StringBuilder sb, ForecastSnapshotBody body, string locationName, Recipient recipient, TimeZoneInfo tz, TemplateSet t, CultureInfo culture, DateTime nowUtc)
    {
        var days = AggregateDays(body, tz, nowUtc).ToList();
        if (days.Count == 0)
            return;  // no forecast blocks → omit the section entirely rather than emit a header-only grid

        sb.Append("<div style=\"background:#ffffff;padding:20px 24px;\">");
        AppendSectionHeading(sb, string.Format(culture, t.Get(Tok.ForecastHeadingFormat), locationName));

        // 24-hour clock legend (WX-190; repositioned WX-184): directly beneath the
        // section rule and above the grid, so a reader meets the explanation before the
        // XX-YY clock bands in the Conditions cell rather than after it. Left-justified
        // and styled like the Current Conditions station line (13px italic #6b8fa8) — the
        // report's other sub-heading caption. (Supersedes WX-195's centered styling that
        // matched the meteogram caption; the two captions now live in different sections.)
        sb.Append($"<div style=\"font-size:13px;color:#6b8fa8;font-style:italic;margin:2px 0 4px;\">{HtmlText(t.Get(Tok.GridTimeLegend))}</div>");

        sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:14px;margin-top:8px;\">");
        sb.Append("<tr style=\"background:#1a3a5c;color:#ffffff;\">");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(t.Get(Tok.ColDate))}</th>");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(t.Get(Tok.ColTemperatures))}</th>");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(t.Get(Tok.ColWind))}</th>");
        sb.Append($"<th style=\"padding:6px 10px;text-align:left;\">{HtmlText(t.Get(Tok.ColConditions))}</th></tr>");

        var row = 0;
        foreach (var day in days)
        {
            var bg = row++ % 2 == 0 ? "#f7f9fc" : "#ffffff";
            var date = day.Date.ToString("ddd MMM d", culture);
            // WX-236: Low before High — the grid's daypart columns run chronologically (00-06 … 18-24),
            // and the Low we report is this morning's minimum (the earliest point of the day), the High
            // the afternoon maximum. Printing min→max matches that time-arrow, so the reader doesn't
            // mistake the morning low for a next-morning low.
            var temps = $"{HtmlText(t.Get(Tok.LowLabel))}: {FormatTempC(day.MinTempC, recipient)}<br/>{HtmlText(t.Get(Tok.HighLabel))}: {FormatTempC(day.MaxTempC, recipient)}";
            var wind = FormatWindKt(day.MaxWindKt, recipient);
            var condHtml = ConditionsCellHtml(day, t);
            sb.Append($"<tr style=\"background:{bg};\">");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(date)}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{temps}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{HtmlText(wind)}</td>");
            sb.Append($"<td style=\"padding:6px 10px;\">{condHtml}</td></tr>");
        }
        sb.Append("</table>");
        sb.Append("</div>");
    }

    // ── closing ───────────────────────────────────────────────────────────────

    private static void AppendClosing(StringBuilder sb, TemplateSet t, string renderedProse)
    {
        sb.Append("<div style=\"background:#f0f4f9;padding:16px 24px;border-top:1px solid #d0dce8;\">");
        sb.Append($"<strong>{HtmlText(t.Get(Tok.InSummaryLabel))}</strong> {renderedProse}");
        sb.Append("</div>");
    }

    private static void AppendSectionHeading(StringBuilder sb, string text) =>
        sb.Append($"<div style=\"font-weight:bold;font-size:17px;color:#1a3a5c;border-bottom:2px solid #1a3a5c;padding-bottom:4px;\">{HtmlText(text)}</div>");

    // ── per-day aggregation ───────────────────────────────────────────────────

    /// <summary>
    /// One local calendar day's forecast.  Wind spans the whole day; high/low span the whole day too
    /// when present, but each is <see langword="null"/> on a partial day that lacks the band holding
    /// that extreme — no afternoon (peak-heating) block → no <see cref="MaxTempC"/>; no pre-dawn/morning
    /// block → no <see cref="MinTempC"/> (WX-234, the same <see cref="DayPartBands"/> gating the
    /// temperature summary uses). The grid renders a suppressed extreme as an em dash.
    /// <see cref="Bands"/> carries only the day's still-live 6-hour blocks in chronological order (one
    /// per local day-part, WX-155; fully-elapsed blocks dropped per WX-195), from which the Conditions
    /// cell tiles the remaining clock bands (WX-190).
    /// </summary>
    private sealed record DaySummary(
        DateOnly Date,
        double? MaxTempC,
        double? MinTempC,
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
    /// A <em>partial</em> day's high or low is <see langword="null"/> when it lacks the band holding
    /// that extreme (no afternoon block → no high; no pre-dawn/morning block → no low), gated via
    /// <see cref="DayPartBands"/> exactly as the temperature summary is, so the two never disagree
    /// (WX-234).
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
            // WX-234: but a PARTIAL day contributes an extreme only if it holds the band that extreme
            // lives in — a high needs the afternoon (peak-heating) block, a low needs a pre-dawn/morning
            // block — else the day's max/min is a pseudo-extreme (a trailing day's morning "high"). The
            // suppressed extreme is null here and renders as an em dash. Shares DayPartBands with the
            // temperature summary (TemperatureRangeSummarizer.DailyHighsLows) so the grid and the prose
            // gate identically and cannot drift.
            bool hasAfternoon = blocks.Any(x => DayPartBands.HasAfternoon(x.Hour));
            bool hasDawnWindow = blocks.Any(x => DayPartBands.HasDawnWindow(x.Hour));
            yield return new DaySummary(
                day,
                hasAfternoon ? blocks.Max(x => x.Block.TemperatureCelsius.Max) : null,
                hasDawnWindow ? blocks.Min(x => x.Block.TemperatureCelsius.Min) : null,
                blocks.Max(x => x.Block.WindKt.Max),
                liveBands);
        }
    }

    // ── deterministic phrase composers ────────────────────────────────────────

    private static string SkyPhrase(IReadOnlyList<SkyLayer> layers, TemplateSet t)
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

        // Only mostly-cloudy (rank 2) and overcast (rank 3) take a low/high prefix; the
        // prefixed form is a single atomic token ("low overcast" / "nublado bajo"), seeded
        // lower-case, so capitalize it to match the cell's leading-capital convention — the
        // English output the current renderer produced via the "Low" prefix word (WX-171).
        if ((rank == 2 || rank == 3) && ceilFeet is int c && (c < 6500 || c > 20000))
        {
            var overcast = rank == 3;
            var low = c < 6500;
            var heightToken = overcast
                ? (low ? Tok.SkyOvercastLow : Tok.SkyOvercastHigh)
                : (low ? Tok.SkyMostlycloudyLow : Tok.SkyMostlycloudyHigh);
            return Capitalize(t.Get(heightToken));
        }

        return rank switch
        {
            1 => t.Get(Tok.SkyPartlyCloudy),
            2 => t.Get(Tok.SkyMostlyCloudy),
            3 => t.Get(Tok.SkyOvercast),
            4 => t.Get(Tok.SkyObscured),
            _ => t.Get(Tok.SkyClear),
        };
    }

    private static string VisibilityPhrase(WeatherSnapshot snap, TemplateSet t)
    {
        // An obscuration names itself (fog/haze/smoke/mist) rather than a band.
        foreach (var w in snap.WeatherPhenomena)
        {
            var named = ObscurationWord(w.Obscuration, t);
            if (named is not null)
                return named;
        }
        if (snap.Cavok)
            return t.Get(Tok.VisGood);
        if (snap.VisibilityStatuteMiles is not double mi)
            return "—";
        return mi switch
        {
            >= 6 => t.Get(Tok.VisGood),
            >= 2 => t.Get(Tok.VisHazy),
            >= 0.5 => t.Get(Tok.VisReduced),
            _ => t.Get(Tok.VisPoor),
        };
    }

    private static string WindPhrase(WeatherSnapshot snap, Recipient recipient, TemplateSet t)
    {
        if ((snap.WindSpeedKt ?? 0) == 0 && snap.WindGustKt is null && !snap.WindIsVariable)
            return t.Get(Tok.WindCalm);

        var speed = FormatWindKt(snap.WindSpeedKt ?? 0, recipient);
        var dir = snap.WindIsVariable || snap.WindDirectionDeg is null
            ? t.Get(Tok.WindVariable)
            : Meteorology.DegreesToCompass(snap.WindDirectionDeg.Value);
        // WindLine "{0} at {1}" + WindGust ", gusting {1}" are atomic format strings; dir
        // and speed are already-formatted strings, so the culture passed to Format is
        // immaterial (no numeric placeholder) — use Inv for determinism.
        var line = string.Format(Inv, t.Get(Tok.WindLine), dir, speed);
        var gust = snap.WindGustKt is int g ? string.Format(Inv, t.Get(Tok.WindGust), FormatWindKt(g, recipient)) : "";
        return $"{line}{gust}";
    }

    private static string WeatherPhrase(IReadOnlyList<SnapshotWeather> phenomena, TemplateSet t)
    {
        var parts = new List<string>();
        foreach (var w in phenomena)
        {
            var p = OneWeatherPhrase(w, t);
            if (p.Length > 0)
                parts.Add(p);
        }
        return parts.Count == 0 ? "" : Capitalize(string.Join(", ", parts));
    }

    private static string OneWeatherPhrase(SnapshotWeather w, TemplateSet t)
    {
        if (w.Descriptor == WeatherDescriptor.Thunderstorm)
            return t.Get(Tok.WxThunderstorm);

        if (w.Precipitation.Count > 0)
            return t.Get(ObservedPrecipToken(w.Precipitation[0], w.Descriptor, w.Intensity));

        return ObscurationWord(w.Obscuration, t) ?? "";
    }

    /// <summary>
    /// The atomic observed-precipitation token for a (type, descriptor, intensity) triple.
    /// Freezing and showers descriptors take precedence over light/heavy intensity (matching
    /// the former composition order); any combination not separately authored
    /// (e.g. heavy drizzle, freezing snow, light wintry mix) falls back to the bare type
    /// token — never empty. Returns a <see cref="Tok"/> constant so every reference stays on
    /// the compile-checked contract; a fallback that resolves to bare is the intended design
    /// (WX-171, Step 2 ruling).
    /// </summary>
    private static string ObservedPrecipToken(PrecipitationType type, WeatherDescriptor? descriptor, WeatherIntensity intensity) => type switch
    {
        PrecipitationType.Drizzle =>
            descriptor == WeatherDescriptor.Freezing ? Tok.DrizzleFreezing
            : intensity == WeatherIntensity.Light ? Tok.DrizzleLight
            : Tok.Drizzle,
        PrecipitationType.Snow or PrecipitationType.SnowGrains =>
            descriptor == WeatherDescriptor.Showers ? Tok.SnowShowers
            : intensity == WeatherIntensity.Light ? Tok.SnowLight
            : intensity == WeatherIntensity.Heavy ? Tok.SnowHeavy
            : Tok.Snow,
        PrecipitationType.IcePellets or PrecipitationType.Hail or PrecipitationType.SmallHail =>
            Tok.WintryMix,   // no intensity/freezing/showers variants authored — bare always
        _ =>  // Rain and any other liquid precipitation
            descriptor == WeatherDescriptor.Freezing ? Tok.RainFreezing
            : descriptor == WeatherDescriptor.Showers ? Tok.RainShowers
            : intensity == WeatherIntensity.Light ? Tok.RainLight
            : intensity == WeatherIntensity.Heavy ? Tok.RainHeavy
            : Tok.Rain,
    };

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
    private static string ConditionsCellHtml(DaySummary day, TemplateSet t)
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
            var (phrase, severe) = BandPhrase(block, t);
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
    /// (which the caller emphasizes).  Severe leads with its atomic noun+outlook token
    /// ("Severe storms likely" / "Severe weather likely"); otherwise a precipitation band
    /// reads its atomic "{phenomenon} {outlook}" token ("Rain likely") and a dry band reads
    /// its sky word ("Clear and dry" when clear).  Capitalized so every tiled line in the
    /// cell opens with a consistent leading capital.
    /// </summary>
    private static (string Phrase, bool Severe) BandPhrase(ForecastSnapshotBlock block, TemplateSet t)
    {
        if (block.SevereFlag)
        {
            // A severe block may carry no precip (a damaging-wind event); the non-convective
            // token covers that, and a missing expectation falls back to "likely". The severe
            // tokens are seeded already-capitalized, so use them verbatim.
            return (t.Get(SevereBandToken(block.PrecipPhenomenon, block.PrecipExpectation)), true);
        }

        if (block.PrecipExpectation != PrecipExpectation.None && block.PrecipPhenomenon is PrecipPhenomenon p)
            return (Capitalize(t.Get(BandPrecipToken(p, block.PrecipExpectation))), false);

        if (block.SkyState == SkyState.Clear)
            return (Capitalize(t.Get(Tok.ClearAndDry)), false);
        return (t.Get(SkyWordToken(block.SkyState)), false);
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
    private static string ProseTiming(DateTime local, TemplateSet t, CultureInfo culture) =>
        $"{local.ToString("dddd", culture)} {ProsePart(local.Hour, t)}";

    private static string ProsePart(int localHour, TemplateSet t) => PartOf(localHour) switch
    {
        DayPart.Morning => Lower(t.Get(Tok.PartMorning)),
        DayPart.Afternoon => Lower(t.Get(Tok.PartAfternoon)),
        DayPart.Evening => Lower(t.Get(Tok.PartEvening)),
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

    /// <summary>The outlook suffix ("possible" / "likely" / "expected") for a precip expectation; <c>Certain</c> reads "expected" (never "certain").</summary>
    private static PrecipExpectation NormalizeOutlook(PrecipExpectation e) =>
        e == PrecipExpectation.None ? PrecipExpectation.Likely : e;

    /// <summary>
    /// The atomic forecast-band token for a non-severe (phenomenon, outlook) pair — the
    /// noun and the agreeing outlook hedge baked into one token ("rain likely",
    /// "lluvia probable"). Rain is the default family for any unhandled phenomenon, matching
    /// the former composition. Every arm is a <see cref="Tok"/> constant (compile-checked).
    /// </summary>
    private static string BandPrecipToken(PrecipPhenomenon phen, PrecipExpectation exp) => (phen, exp) switch
    {
        (PrecipPhenomenon.Thunderstorm, PrecipExpectation.Possible) => Tok.StormsPossible,
        (PrecipPhenomenon.Thunderstorm, PrecipExpectation.Likely) => Tok.StormsLikely,
        (PrecipPhenomenon.Thunderstorm, _) => Tok.StormsExpected,
        (PrecipPhenomenon.Snow, PrecipExpectation.Possible) => Tok.SnowPossible,
        (PrecipPhenomenon.Snow, PrecipExpectation.Likely) => Tok.SnowLikely,
        (PrecipPhenomenon.Snow, _) => Tok.SnowExpected,
        (PrecipPhenomenon.Mixed, PrecipExpectation.Possible) => Tok.WmixPossible,
        (PrecipPhenomenon.Mixed, PrecipExpectation.Likely) => Tok.WmixLikely,
        (PrecipPhenomenon.Mixed, _) => Tok.WmixExpected,
        (PrecipPhenomenon.FreezingPrecip, PrecipExpectation.Possible) => Tok.FzraPossible,
        (PrecipPhenomenon.FreezingPrecip, PrecipExpectation.Likely) => Tok.FzraLikely,
        (PrecipPhenomenon.FreezingPrecip, _) => Tok.FzraExpected,
        (_, PrecipExpectation.Possible) => Tok.RainPossible,
        (_, PrecipExpectation.Likely) => Tok.RainLikely,
        (_, _) => Tok.RainExpected,
    };

    /// <summary>
    /// The atomic severe-band token for a (phenomenon, outlook) pair: convective
    /// (thunderstorm) → <c>sev_storms_*</c>, otherwise → <c>sev_wx_*</c> (covers a no-precip
    /// damaging-wind event). A <c>None</c> expectation defaults to "likely". Each arm is a
    /// <see cref="Tok"/> constant (compile-checked).
    /// </summary>
    private static string SevereBandToken(PrecipPhenomenon? phen, PrecipExpectation exp) => (phen == PrecipPhenomenon.Thunderstorm, NormalizeOutlook(exp)) switch
    {
        (true, PrecipExpectation.Possible) => Tok.SevStormsPossible,
        (true, PrecipExpectation.Likely) => Tok.SevStormsLikely,
        (true, _) => Tok.SevStormsExpected,
        (false, PrecipExpectation.Possible) => Tok.SevWxPossible,
        (false, PrecipExpectation.Likely) => Tok.SevWxLikely,
        (false, _) => Tok.SevWxExpected,
    };

    private static string SkyWordToken(SkyState s) => s switch
    {
        SkyState.PartlyCloudy => Tok.SkyPartlyCloudy,
        SkyState.MostlyCloudy => Tok.SkyMostlyCloudy,
        SkyState.Overcast => Tok.SkyOvercast,
        _ => Tok.SkyClear,
    };

    private static string? ObscurationWord(WeatherObscuration? o, TemplateSet t) => o switch
    {
        WeatherObscuration.Fog => t.Get(Tok.WxFog),
        WeatherObscuration.Mist => t.Get(Tok.WxMist),
        WeatherObscuration.Haze => t.Get(Tok.WxHaze),
        WeatherObscuration.Smoke => t.Get(Tok.WxSmoke),
        _ => null,
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

    // WX-234: a gated-out daily extreme (a partial day with no afternoon block has no high; with no
    // pre-dawn/morning block, no low — see DayPartBands) renders as an em dash rather than a
    // pseudo-temperature. Language-neutral punctuation, so no localized token is needed.
    private static string FormatTempC(double? celsius, Recipient r) =>
        celsius is double c ? FormatTempC(c, r) : "—";

    private static string FormatTempC(double celsius, Recipient r) =>
        r.TempUnit == "C"
            ? $"{Math.Round(celsius).ToString("0", Inv)}°C"
            : $"{Math.Round(celsius * 9.0 / 5.0 + 32.0).ToString("0", Inv)}°F";

    // WX-228: a temperature RANGE token ({q:temp_range:lo:hi}, canonical °C) — both
    // endpoints converted and rounded independently to the recipient's unit and joined
    // with an en-dash under a single unit suffix ("24–26°C" / "75–79°F"). Endpoints
    // are ordered defensively, and a pair that collapses after rounding (e.g. a ±1 °C
    // flat-week band that rounds to one °F value) renders as a single figure.
    private static string FormatTempRangeC(double loC, double hiC, Recipient r)
    {
        bool celsius = r.TempUnit == "C";
        double lo = Math.Round(celsius ? loC : loC * 9.0 / 5.0 + 32.0);
        double hi = Math.Round(celsius ? hiC : hiC * 9.0 / 5.0 + 32.0);
        if (lo > hi)
            (lo, hi) = (hi, lo);
        string unit = celsius ? "°C" : "°F";
        return lo == hi
            ? $"{lo.ToString("0", Inv)}{unit}"
            : $"{lo.ToString("0", Inv)}–{hi.ToString("0", Inv)}{unit}";
    }

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