using System.Reflection;

using log4net;

using MetarParser.Data;
using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// WX-171 token contract: one constant per <c>LanguageTemplates</c> token the
/// renderer can reference. The renderer uses ONLY these constants (never string
/// literals), so referencing an undeclared token is a compile error. A CI parity
/// test (<c>TokSeedParityTests</c>) asserts this set matches the en/es seed
/// exactly (no gaps, no orphans), and a runtime completeness check verifies every
/// constant resolves for each supported language. Generated from the seed; keep
/// in lockstep with it.
/// </summary>
public static class Tok
{
    public const string AndConjunction = "AndConjunction";
    public const string ChangeTempNoun = "ChangeTempNoun";
    public const string ChangeWindNoun = "ChangeWindNoun";
    public const string ClosingFallback = "ClosingFallback";
    public const string ColConditions = "ColConditions";
    public const string ColDate = "ColDate";
    public const string ColTemperatures = "ColTemperatures";
    public const string ColWind = "ColWind";
    public const string CondSevereStorms = "CondSevereStorms";
    public const string CondSevereWeather = "CondSevereWeather";
    public const string CurrentConditionsHeading = "CurrentConditionsHeading";
    public const string DiagnosticLabel = "DiagnosticLabel";
    public const string DiagnosticSubject = "DiagnosticSubject";
    public const string DirAppearing = "DirAppearing";
    public const string DirClearing = "DirClearing";
    public const string DirStrengthening = "DirStrengthening";
    public const string DirWeakening = "DirWeakening";
    public const string ForecastHeadingFormat = "ForecastHeadingFormat";
    public const string GridTimeLegend = "GridTimeLegend";
    public const string HazardBannerFormat = "HazardBannerFormat";
    public const string HighLabel = "HighLabel";
    public const string InSummaryLabel = "InSummaryLabel";
    public const string LowLabel = "LowLabel";
    public const string MeteogramAlt = "MeteogramAlt";
    public const string MeteogramCaption = "MeteogramCaption";
    public const string NoObservationNote = "NoObservationNote";
    public const string PartAfternoon = "PartAfternoon";
    public const string PartEvening = "PartEvening";
    public const string PartMorning = "PartMorning";
    public const string RowHumidity = "RowHumidity";
    public const string RowPressure = "RowPressure";
    public const string RowSky = "RowSky";
    public const string RowTemperature = "RowTemperature";
    public const string RowVisibility = "RowVisibility";
    public const string RowWeather = "RowWeather";
    public const string RowWind = "RowWind";
    public const string ScheduledReportLabel = "ScheduledReportLabel";
    public const string ScheduledReportSubject = "ScheduledReportSubject";
    public const string SkyClear = "SkyClear";
    public const string SkyMostlyCloudy = "SkyMostlyCloudy";
    public const string SkyObscured = "SkyObscured";
    public const string SkyOvercast = "SkyOvercast";
    public const string SkyPartlyCloudy = "SkyPartlyCloudy";
    public const string StationSubtitle = "StationSubtitle";
    public const string SubjectForConnective = "SubjectForConnective";
    public const string UnscheduledUpdateLabel = "UnscheduledUpdateLabel";
    public const string UnscheduledUpdateSubject = "UnscheduledUpdateSubject";
    public const string VisGood = "VisGood";
    public const string VisHazy = "VisHazy";
    public const string VisPoor = "VisPoor";
    public const string VisReduced = "VisReduced";
    public const string WelcomeFormat = "WelcomeFormat";
    public const string WelcomeGreeting = "WelcomeGreeting";
    public const string WelcomeSubject = "WelcomeSubject";
    public const string WhatsChangedLabel = "WhatsChangedLabel";
    public const string WindCalm = "WindCalm";
    public const string WindGust = "WindGust";
    public const string WindLine = "WindLine";
    public const string WindVariable = "WindVariable";
    public const string WxFog = "WxFog";
    public const string WxHaze = "WxHaze";
    public const string WxMist = "WxMist";
    public const string WxSmoke = "WxSmoke";
    public const string WxThunderstorm = "WxThunderstorm";
    public const string ClearAndDry = "clear_and_dry";
    public const string Drizzle = "drizzle";
    public const string DrizzleFreezing = "drizzle_freezing";
    public const string DrizzleLight = "drizzle_light";
    public const string FzraExpected = "fzra_expected";
    public const string FzraLikely = "fzra_likely";
    public const string FzraPossible = "fzra_possible";
    // WX-224: in-image meteogram chart labels. The name strings live in MetarParser.Data
    // (shared) so MeteogramWorker can reference them without a WxVis->WxReport dependency.
    public const string MeteogramRh = MeteogramTokens.Rh;
    public const string MeteogramTemp = MeteogramTokens.Temp;
    public const string MeteogramWind = MeteogramTokens.Wind;
    public const string Rain = "rain";
    public const string RainExpected = "rain_expected";
    public const string RainFreezing = "rain_freezing";
    public const string RainHeavy = "rain_heavy";
    public const string RainLight = "rain_light";
    public const string RainLikely = "rain_likely";
    public const string RainPossible = "rain_possible";
    public const string RainShowers = "rain_showers";
    public const string SevStormsExpected = "sev_storms_expected";
    public const string SevStormsLikely = "sev_storms_likely";
    public const string SevStormsPossible = "sev_storms_possible";
    public const string SevWxExpected = "sev_wx_expected";
    public const string SevWxLikely = "sev_wx_likely";
    public const string SevWxPossible = "sev_wx_possible";
    public const string SkyMostlycloudyHigh = "sky_mostlycloudy_high";
    public const string SkyMostlycloudyLow = "sky_mostlycloudy_low";
    public const string SkyOvercastHigh = "sky_overcast_high";
    public const string SkyOvercastLow = "sky_overcast_low";
    public const string Snow = "snow";
    public const string SnowExpected = "snow_expected";
    public const string SnowHeavy = "snow_heavy";
    public const string SnowLight = "snow_light";
    public const string SnowLikely = "snow_likely";
    public const string SnowPossible = "snow_possible";
    public const string SnowShowers = "snow_showers";
    public const string StormsExpected = "storms_expected";
    public const string StormsLikely = "storms_likely";
    public const string StormsPossible = "storms_possible";
    public const string WintryMix = "wintry_mix";
    public const string WmixExpected = "wmix_expected";
    public const string WmixLikely = "wmix_likely";
    public const string WmixPossible = "wmix_possible";

    // WX-238: standalone phenomenon + probability vocabulary for the free-composed narrative. The
    // deterministic renderer uses the fused composites above (e.g. sev_storms_possible) because it
    // can't glue phrases safely across languages; the narrative (where Claude handles grammar)
    // anchors on these SEPARATED axes instead — phenomenon ("severe storms") + probability
    // ("possible") — so it composes "severe storms are possible" from approved parts. These are
    // never rendered; they exist as first-class vocabulary (completeness-checked, seeded in every
    // language) purely for the prompt glossary.
    public const string Storms = "storms";
    public const string SevereStorms = "severe_storms";
    public const string SevereWeather = "severe_weather";
    public const string ProbPossible = "possible";
    public const string ProbLikely = "likely";
    public const string ProbExpected = "expected";

    private static readonly IReadOnlySet<string> _all =
        typeof(Tok).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every declared token value — the renderer's full required-token contract, consumed by the completeness checks (startup, send gate, WX-172).</summary>
    public static IReadOnlySet<string> All => _all;
}

/// <summary>
/// Which surface a report-kind word is destined for: the email <see cref="Title"/>
/// (subject line) or the rendered <see cref="Header"/> label inside the report. The
/// two read differently for the same kind ("Weather Report" vs "Scheduled Report"),
/// so <see cref="ReportLabels.TokenFor"/> takes this to pick the right token.
/// </summary>
public enum LabelType
{
    /// <summary>The email subject word — e.g. "Weather Report" / "Weather Update".</summary>
    Title,

    /// <summary>The italic report-type label rendered in the header — e.g. "Scheduled Report" / "Unscheduled Update".</summary>
    Header,
}

/// <summary>
/// Maps the deterministic report enums to their <see cref="Tok"/> token constants — the
/// kind→word and severe-phenomenon→noun mappings that the renderer, the subject builder,
/// and the WX-156 subject prefix all share (WX-171). Centralizing them here keeps the
/// subject and the body from ever naming the same thing differently, and keeps every
/// renderer-reachable token reference on the compile-checked <see cref="Tok"/> contract.
/// </summary>
public static class ReportLabels
{
    private static readonly ILog Logger = LogManager.GetLogger(typeof(ReportLabels));

    /// <summary>
    /// The <see cref="Tok"/> token a <see cref="ReportKind"/> renders to on either surface:
    /// the email subject (<see cref="LabelType.Title"/>) or the in-report header label
    /// (<see cref="LabelType.Header"/>). A future <see cref="ReportKind"/> or
    /// <see cref="LabelType"/> not handled falls to the Scheduled word but logs a warning,
    /// so it surfaces without ever failing a send over a cosmetic label.
    /// </summary>
    public static string TokenFor(ReportKind kind, LabelType labelType) => (labelType, kind) switch
    {
        (LabelType.Title, ReportKind.Scheduled) => Tok.ScheduledReportSubject,
        (LabelType.Title, ReportKind.Unscheduled) => Tok.UnscheduledUpdateSubject,
        (LabelType.Title, ReportKind.Diagnostic) => Tok.DiagnosticSubject,
        (LabelType.Header, ReportKind.Scheduled) => Tok.ScheduledReportLabel,
        (LabelType.Header, ReportKind.Unscheduled) => Tok.UnscheduledUpdateLabel,
        (LabelType.Header, ReportKind.Diagnostic) => Tok.DiagnosticLabel,
        _ => DefaultFor(kind, labelType),
    };

    private static string DefaultFor(ReportKind kind, LabelType labelType)
    {
        Logger.Warn($"ReportLabels.TokenFor: unhandled (kind={kind}, labelType={labelType}); defaulting to the scheduled {labelType}.");
        return labelType == LabelType.Title ? Tok.ScheduledReportSubject : Tok.ScheduledReportLabel;
    }

    /// <summary>
    /// The severe lead-noun token: convective (<see cref="Tok.CondSevereStorms"/>,
    /// "Severe storms") for a thunderstorm, generic (<see cref="Tok.CondSevereWeather"/>,
    /// "Severe weather") otherwise — a severe block can be a damaging-wind event with no
    /// precip. Single source for both the body hazard banner and the WX-156 subject prefix,
    /// so they never disagree.
    /// </summary>
    public static string SevereNounToken(PrecipPhenomenon? phenomenon) =>
        phenomenon == PrecipPhenomenon.Thunderstorm ? Tok.CondSevereStorms : Tok.CondSevereWeather;
}