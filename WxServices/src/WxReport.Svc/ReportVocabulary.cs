using System.Globalization;

using log4net;

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// Which surface a report-kind word is destined for: the email <see cref="Title"/>
/// (subject line) or the rendered <see cref="Header"/> label inside the report.
/// The two read differently for the same kind ("Weather Report" vs "Scheduled
/// Report"), so <see cref="ReportVocabulary.GetFromReportKind"/> takes this to
/// pick the right column.
/// </summary>
public enum LabelType
{
    /// <summary>The email subject word — e.g. "Weather Report" / "Weather Update".</summary>
    Title,

    /// <summary>The italic report-type label rendered in the header — e.g. "Scheduled Report" / "Unscheduled Update".</summary>
    Header,
}

/// <summary>
/// The language-specific words and labels the WX-129 deterministic renderer
/// substitutes into the report scaffold — section headings, table labels, and
/// the small controlled vocabularies for sky / weather / visibility / per-day
/// conditions phrasing.  The renderer owns the <em>logic</em> (which word to
/// pick from the observation and forecast); this record owns the <em>strings</em>,
/// so a new language is a new <see cref="ReportVocabulary"/> instance plus a
/// <see cref="CultureInfo"/> — the seed of the WX-137 language registry.
///
/// <para>
/// Narrative prose (<c>changeSummary</c>, <c>closing</c>) is authored per-language
/// by Claude (WX-128) and is <em>not</em> here; only the deterministic chrome is.
/// </para>
///
/// <para>
/// <b>Spanish (<see cref="Es"/>) is a draft pending a correctness pass</b> before
/// it reaches production Spanish recipients (WX-129 grooming).  The English
/// (<see cref="En"/>) vocabulary is final.
/// </para>
/// </summary>
public sealed record ReportVocabulary
{
    // ── section headings / band labels ──────────────────────────────────────
    public required string CurrentConditionsHeading { get; init; }
    public required string StationAtPrefix { get; init; }          // "at <station>" subtitle — "at" / "en"
    public required string ForecastHeadingFormat { get; init; }   // {0} = locality name
    public required string WhatsChangedLabel { get; init; }
    public required string InSummaryLabel { get; init; }
    public required string ScheduledReportLabel { get; init; }
    public required string UnscheduledUpdateLabel { get; init; }
    public required string DiagnosticLabel { get; init; }
    // ── email subject words (LabelType.Title) — read differently from the header labels above ─
    public required string ScheduledReportSubject { get; init; }     // "Weather Report"
    public required string UnscheduledUpdateSubject { get; init; }    // "Weather Update"
    public required string DiagnosticSubject { get; init; }          // "Diagnostic"
    public required string SubjectForConnective { get; init; }        // joins the recipient name in the subject — "for" / "para"
    public required string NoObservationNote { get; init; }

    // ── first-report welcome (WX-130; standalone welcome-only email) ──────────
    public required string WelcomeSubject { get; init; }           // subject prefix; locality appended by the worker
    public required string WelcomeGreeting { get; init; }          // bold opening line
    public required string WelcomeFormat { get; init; }            // {0} = locality name, {1} = localized list of scheduled send times
    public required string AndConjunction { get; init; }           // joins the last two send times — "and" / "y"

    // ── current-conditions row labels ───────────────────────────────────────
    public required string RowSky { get; init; }
    public required string RowVisibility { get; init; }
    public required string RowWind { get; init; }
    public required string RowWeather { get; init; }
    public required string RowTemperature { get; init; }
    public required string RowHumidity { get; init; }
    public required string RowPressure { get; init; }

    // ── extended-forecast column headers + temp labels ──────────────────────
    public required string ColDate { get; init; }
    public required string ColTemperatures { get; init; }
    public required string ColWind { get; init; }
    public required string ColConditions { get; init; }
    public required string HighLabel { get; init; }
    public required string LowLabel { get; init; }

    // ── sky coverage + height prefixes ──────────────────────────────────────
    public required string SkyClear { get; init; }
    public required string SkyPartlyCloudy { get; init; }
    public required string SkyMostlyCloudy { get; init; }
    public required string SkyOvercast { get; init; }
    public required string SkyObscured { get; init; }
    public required string LowPrefix { get; init; }   // "Low …"
    public required string HighPrefix { get; init; }  // "High …"

    // ── visibility qualitative bands (thresholds live in the renderer) ──────
    public required string VisGood { get; init; }
    public required string VisHazy { get; init; }
    public required string VisReduced { get; init; }
    public required string VisPoor { get; init; }

    // ── wind ────────────────────────────────────────────────────────────────
    public required string WindCalm { get; init; }
    public required string WindVariable { get; init; }
    public required string WindAt { get; init; }       // "{dir} at {speed}"
    public required string WindGusting { get; init; }  // ", gusting {speed}"

    // ── weather phenomena (plain-language, reader-facing) ───────────────────
    public required string WxLight { get; init; }
    public required string WxHeavy { get; init; }
    public required string WxRain { get; init; }
    public required string WxDrizzle { get; init; }
    public required string WxSnow { get; init; }
    public required string WxThunderstorm { get; init; }
    public required string WxFreezing { get; init; }   // "Freezing {precip}"
    public required string WxShowers { get; init; }     // "{precip} showers"
    public required string WxFog { get; init; }
    public required string WxMist { get; init; }
    public required string WxHaze { get; init; }
    public required string WxSmoke { get; init; }
    public required string WxMixed { get; init; }       // wintry mix / sleet

    // ── per-day conditions: day-part labels, when-adverbials, outlook, connectives ─
    public required string CondAndDry { get; init; }        // dry day: "{sky} and dry" when clear, else just the sky word
    // Capitalized day-part LABELS for the per-day conditions grid lines ("Afternoon — rain likely").
    public required string PartMorning { get; init; }
    public required string PartAfternoon { get; init; }
    public required string PartEvening { get; init; }
    public required string PartOvernight { get; init; }
    // Mid-sentence WHEN-adverbials for the severe one-liner ("Severe storms likely in the afternoon").
    public required string CondMorning { get; init; }
    public required string CondAfternoon { get; init; }
    public required string CondEvening { get; init; }
    public required string CondOvernight { get; init; }
    public required string OutlookPossible { get; init; }   // hedge: "possible"
    public required string OutlookLikely { get; init; }     // hedge: "likely"
    public required string OutlookExpected { get; init; }   // hedge for Certain — never "certain"
    public required string CondSevereStorms { get; init; }  // severe lead, convective: "Severe storms"
    public required string CondSevereWeather { get; init; } // severe lead, non-convective (e.g. damaging wind, no precip): "Severe weather"
    public required string CondThen { get; init; }          // joins the two clauses of a severe day: "then"
    public required string Storms { get; init; }            // generic "storms"
    public required string HazardBannerFormat { get; init; } // degraded safety send: {0} = severe phrase, {1} = "Saturday afternoon"

    /// <summary>The IETF culture used for date/time names and number formatting. Localized to the language; number conventions stay US/period-decimal until WX-138 (so <c>es-US</c>, not <c>es-ES</c>).</summary>
    public required CultureInfo Culture { get; init; }

    private static readonly ILog Logger = LogManager.GetLogger(typeof(ReportVocabulary));

    /// <summary>
    /// The single source of truth (WX-154) for the localized word a <see cref="ReportKind"/>
    /// renders to, on either surface: the email subject (<see cref="LabelType.Title"/>) or
    /// the in-report header label (<see cref="LabelType.Header"/>). Centralizing the
    /// kind→word mapping here is what keeps the subject and the header from drifting apart
    /// — they read from the same switch. A future <see cref="ReportKind"/> added without
    /// updating this method falls to the Scheduled word but logs a warning, so it surfaces
    /// without ever failing a send over a cosmetic label.
    /// </summary>
    public string GetFromReportKind(ReportKind kind, LabelType labelType)
    {
        switch (labelType, kind)
        {
            case (LabelType.Title, ReportKind.Scheduled): return ScheduledReportSubject;
            case (LabelType.Title, ReportKind.Unscheduled): return UnscheduledUpdateSubject;
            case (LabelType.Title, ReportKind.Diagnostic): return DiagnosticSubject;
            case (LabelType.Header, ReportKind.Scheduled): return ScheduledReportLabel;
            case (LabelType.Header, ReportKind.Unscheduled): return UnscheduledUpdateLabel;
            case (LabelType.Header, ReportKind.Diagnostic): return DiagnosticLabel;
            default:
                // A future ReportKind or LabelType not handled above lands here: log it and
                // fall back to the scheduled word for the requested surface, so the send
                // never fails over a cosmetic label.
                Logger.Warn($"GetFromReportKind: unhandled (kind={kind}, labelType={labelType}); defaulting to the scheduled {labelType}.");
                return labelType == LabelType.Title ? ScheduledReportSubject : ScheduledReportLabel;
        }
    }

    /// <summary>Returns the vocabulary for an ISO 639-1 code. Falls back to <see cref="En"/> only defensively — a path that should be unreachable once WX-137 gates recipients to <see cref="SupportedCodes"/>.</summary>
    public static ReportVocabulary ForLanguage(string isoCode) =>
        ByCode.TryGetValue(isoCode, out var v) ? v : En;

    private static CultureInfo SafeCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>English vocabulary — final.</summary>
    public static readonly ReportVocabulary En = new()
    {
        CurrentConditionsHeading = "Current Conditions",
        StationAtPrefix = "at",
        ForecastHeadingFormat = "Forecast for {0}",
        WhatsChangedLabel = "What's changed:",
        InSummaryLabel = "In summary:",
        ScheduledReportLabel = "Scheduled Report",
        UnscheduledUpdateLabel = "Unscheduled Update",
        DiagnosticLabel = "Diagnostic",
        ScheduledReportSubject = "Weather Report",
        UnscheduledUpdateSubject = "Weather Update",
        DiagnosticSubject = "Diagnostic",
        SubjectForConnective = "for",
        NoObservationNote = "No recent observation is available from any station within about 30 miles, so the report below is based on forecast model data only.",
        WelcomeSubject = "Welcome to WxReport",
        WelcomeGreeting = "Welcome to WxReport!",
        WelcomeFormat = "From now on you'll receive a daily weather report for {0} at {1} local time, plus extra alerts whenever the weather changes significantly. We're glad to have you.",
        AndConjunction = "and",
        RowSky = "Sky",
        RowVisibility = "Visibility",
        RowWind = "Wind",
        RowWeather = "Weather",
        RowTemperature = "Temperature",
        RowHumidity = "Relative Humidity",
        RowPressure = "Pressure",
        ColDate = "Date",
        ColTemperatures = "Temperatures",
        ColWind = "Wind",
        ColConditions = "Conditions",
        HighLabel = "High",
        LowLabel = "Low",
        SkyClear = "Clear",
        SkyPartlyCloudy = "Partly cloudy",
        SkyMostlyCloudy = "Mostly cloudy",
        SkyOvercast = "Overcast",
        SkyObscured = "Sky obscured",
        LowPrefix = "Low",
        HighPrefix = "High",
        VisGood = "Good",
        VisHazy = "Hazy",
        VisReduced = "Reduced",
        VisPoor = "Poor",
        WindCalm = "Calm",
        WindVariable = "Variable",
        WindAt = "at",
        WindGusting = "gusting",
        WxLight = "Light",
        WxHeavy = "Heavy",
        WxRain = "rain",
        WxDrizzle = "drizzle",
        WxSnow = "snow",
        WxThunderstorm = "Thunderstorm",
        WxFreezing = "Freezing",
        WxShowers = "showers",
        WxFog = "Fog",
        WxMist = "Mist",
        WxHaze = "Haze",
        WxSmoke = "Smoke",
        WxMixed = "Wintry mix",
        CondAndDry = "and dry",
        PartMorning = "Morning",
        PartAfternoon = "Afternoon",
        PartEvening = "Evening",
        PartOvernight = "Overnight",
        CondMorning = "in the morning",
        CondAfternoon = "in the afternoon",
        CondEvening = "in the evening",
        CondOvernight = "overnight",
        OutlookPossible = "possible",
        OutlookLikely = "likely",
        OutlookExpected = "expected",
        CondSevereStorms = "Severe storms",
        CondSevereWeather = "Severe weather",
        CondThen = "then",
        Storms = "storms",
        HazardBannerFormat = "{0} in your forecast — {1}.",
        Culture = SafeCulture("en-US"),
    };

    /// <summary>Spanish vocabulary — DRAFT, pending a correctness pass before production use (WX-129).</summary>
    public static readonly ReportVocabulary Es = new()
    {
        CurrentConditionsHeading = "Condiciones actuales",
        StationAtPrefix = "en",
        ForecastHeadingFormat = "Pronóstico para {0}",
        WhatsChangedLabel = "Qué ha cambiado:",
        InSummaryLabel = "En resumen:",
        ScheduledReportLabel = "Reporte programado",
        UnscheduledUpdateLabel = "Actualización no programada",
        DiagnosticLabel = "Diagnóstico",
        ScheduledReportSubject = "Reporte del tiempo",
        UnscheduledUpdateSubject = "Actualización del tiempo",
        DiagnosticSubject = "Diagnóstico",
        SubjectForConnective = "para",
        NoObservationNote = "No hay una observación reciente de ninguna estación a unos 50 km, por lo que el informe a continuación se basa únicamente en datos del modelo de pronóstico.",
        WelcomeSubject = "Bienvenido a WxReport",
        WelcomeGreeting = "¡Bienvenido a WxReport!",
        WelcomeFormat = "A partir de ahora recibirá un informe del tiempo diario para {0} a las {1} hora local, además de alertas adicionales cuando el tiempo cambie significativamente. Nos alegra tenerle con nosotros.",
        AndConjunction = "y",
        RowSky = "Cielo",
        RowVisibility = "Visibilidad",
        RowWind = "Viento",
        RowWeather = "Tiempo",
        RowTemperature = "Temperatura",
        RowHumidity = "Humedad relativa",
        RowPressure = "Presión",
        ColDate = "Fecha",
        ColTemperatures = "Temperaturas",
        ColWind = "Viento",
        ColConditions = "Condiciones",
        HighLabel = "Máx",
        LowLabel = "Mín",
        SkyClear = "Despejado",
        SkyPartlyCloudy = "Parcialmente nublado",
        SkyMostlyCloudy = "Mayormente nublado",
        SkyOvercast = "Nublado",
        SkyObscured = "Cielo cubierto",
        LowPrefix = "Bajo",
        HighPrefix = "Alto",
        VisGood = "Buena",
        VisHazy = "Brumosa",
        VisReduced = "Reducida",
        VisPoor = "Mala",
        WindCalm = "Calma",
        WindVariable = "Variable",
        WindAt = "a",
        WindGusting = "con ráfagas de",
        WxLight = "Ligera",
        WxHeavy = "Fuerte",
        WxRain = "lluvia",
        WxDrizzle = "llovizna",
        WxSnow = "nieve",
        WxThunderstorm = "Tormenta",
        WxFreezing = "Helada",
        WxShowers = "chubascos de",
        WxFog = "Niebla",
        WxMist = "Neblina",
        WxHaze = "Calima",
        WxSmoke = "Humo",
        WxMixed = "Mezcla invernal",
        CondAndDry = "y seco",
        PartMorning = "Mañana",
        PartAfternoon = "Tarde",
        PartEvening = "Noche",
        PartOvernight = "Madrugada",
        CondMorning = "por la mañana",
        CondAfternoon = "por la tarde",
        CondEvening = "al anochecer",
        CondOvernight = "de madrugada",
        OutlookPossible = "posibles",
        OutlookLikely = "probables",
        OutlookExpected = "previstas",
        CondSevereStorms = "Tormentas severas",
        CondSevereWeather = "Tiempo severo",
        CondThen = "luego",
        Storms = "tormentas",
        HazardBannerFormat = "{0} en su pronóstico — {1}.",
        Culture = SafeCulture("es-US"),
    };

    /// <summary>The vocabularies by ISO 639-1 code — the single registry the renderer and the WX-137 supported-language gate read. English holds no privileged status here: it is one entry beside the others, mapping the same concepts to its own words.</summary>
    private static readonly IReadOnlyDictionary<string, ReportVocabulary> ByCode =
        new Dictionary<string, ReportVocabulary>(StringComparer.Ordinal) { ["en"] = En, ["es"] = Es };

    /// <summary>
    /// ISO 639-1 codes the renderer has a vocabulary for.  WX-137's
    /// supported-language gate asserts a language is in this set before a
    /// recipient can be assigned it — enabling a language (AllLanguages →
    /// SupportedLanguages) requires its templates to exist here, so no recipient
    /// is ever assigned a language the renderer cannot dress.
    /// </summary>
    public static IReadOnlySet<string> SupportedCodes { get; } =
        ByCode.Keys.ToHashSet(StringComparer.Ordinal);
}