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
    public const string ClauseJoin = "ClauseJoin";
    public const string ColConditions = "ColConditions";
    public const string ColDate = "ColDate";
    public const string ColTemperatures = "ColTemperatures";
    public const string ColWind = "ColWind";
    public const string CurrentConditionsHeading = "CurrentConditionsHeading";
    public const string DiagnosticLabel = "DiagnosticLabel";
    public const string DiagnosticSubject = "DiagnosticSubject";
    public const string EpisodeLine = "EpisodeLine";
    public const string EpisodeRange = "EpisodeRange";
    public const string ForecastHeadingFormat = "ForecastHeadingFormat";
    public const string GridTimeLegend = "GridTimeLegend";
    public const string HazardBannerFormat = "HazardBannerFormat";
    public const string HighLabel = "HighLabel";
    public const string InSummaryLabel = "InSummaryLabel";
    public const string LowLabel = "LowLabel";
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
    public const string SevereClause = "SevereClause";
    public const string SkyClear = "SkyClear";
    public const string SkyMostlyCloudy = "SkyMostlyCloudy";
    public const string SkyObscured = "SkyObscured";
    public const string SkyOvercast = "SkyOvercast";
    public const string SkyPartlyCloudy = "SkyPartlyCloudy";
    public const string StationSubtitle = "StationSubtitle";
    public const string Storms = "Storms";
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
    public const string ClearAndDry = "clear_and_dry";
    public const string Drizzle = "drizzle";
    public const string DrizzleFreezing = "drizzle_freezing";
    public const string DrizzleLight = "drizzle_light";
    public const string FzraExpected = "fzra_expected";
    public const string FzraLikely = "fzra_likely";
    public const string FzraPossible = "fzra_possible";
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
}