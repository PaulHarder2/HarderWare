namespace MetarParser.Data;

/// <summary>
/// The in-image meteogram label token names (WX-224). Defined here in
/// <c>MetarParser.Data</c> — shared — so both WxReport's <c>Tok</c> contract and
/// WxVis.Svc's <c>MeteogramWorker</c> reference one source without a
/// WxVis&#8594;WxReport dependency. The chart's Wind / RH / temperature labels are
/// localized per language via these <c>LanguageTemplates</c> tokens; the day-of-week
/// abbreviations come from <c>CultureInfo</c>, not a token.
/// </summary>
public static class MeteogramTokens
{
    public const string Wind = "meteogram_wind";
    public const string Rh = "meteogram_rh";
    public const string Temp = "meteogram_temp";
}