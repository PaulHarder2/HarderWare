using System.Globalization;

using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-256: soft (cosmetic) tokens — noon/midnight — are exempt from the fail-closed suppression
// gates. A language missing ONLY a soft token still sends (the renderer degrades to the culture
// 12-hour form); a missing HARD token still suppresses. These lock that contract and the two
// clock-format contexts (event vs schedule).
public class SoftTokenTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    // A store where language "xx" has every token EXCEPT those named in `omit`; "en" is always complete.
    private static LanguageTemplateStore StoreOmitting(params string[] omit)
    {
        var en = new Language { Id = 1, IsoCode = "en", DisplayName = "English", CultureName = "en-US" };
        var xx = new Language { Id = 2, IsoCode = "xx", DisplayName = "Test", CultureName = "en-US" };
        var rows = new List<LanguageTemplate>();
        foreach (var tok in Tok.All)
        {
            rows.Add(new LanguageTemplate { LanguageId = 1, Language = en, Token = tok, Phrase = tok, Representable = true });
            if (!omit.Contains(tok))
                rows.Add(new LanguageTemplate { LanguageId = 2, Language = xx, Token = tok, Phrase = tok, Representable = true });
        }
        return new LanguageTemplateStore(() => rows);
    }

    // ── the token classification ──

    [Fact]
    public void Soft_IsNoonAndMidnight_StillRealTokens()
    {
        Assert.Contains(Tok.Noon, Tok.Soft);
        Assert.Contains(Tok.Midnight, Tok.Soft);
        Assert.Contains(Tok.Noon, Tok.All);       // seeded, parity-checked, top-up-generated like any token
        Assert.Contains(Tok.Midnight, Tok.All);
    }

    [Fact]
    public void Required_Is_All_Minus_Soft_And_Keeps_Hazards()
    {
        Assert.True(Tok.Required.SetEquals(Tok.All.Except(Tok.Soft)));
        Assert.DoesNotContain(Tok.Noon, Tok.Required);
        Assert.DoesNotContain(Tok.Midnight, Tok.Required);
        Assert.Contains(Tok.CondSevereStorms, Tok.Required);   // safety tokens stay hard-required
        Assert.Contains(Tok.HazardBannerFormat, Tok.Required);
    }

    // ── the send gate ──

    [Fact]
    public void MissingOnlySoftTokens_PassesSendGate_ButAllContractFlagsThem()
    {
        var store = StoreOmitting(Tok.Noon, Tok.Midnight);
        Assert.Empty(store.MissingTokens("xx", Tok.Required));      // sendable — soft tokens don't gate
        var all = store.MissingTokens("xx", Tok.All);
        Assert.Contains(Tok.Noon, all);                            // top-up still knows to fill them
        Assert.Contains(Tok.Midnight, all);
    }

    [Fact]
    public void MissingAHardToken_StillSuppresses()  // regression: safety not weakened
    {
        var store = StoreOmitting(Tok.CondSevereStorms);
        Assert.Contains(Tok.CondSevereStorms, store.MissingTokens("xx", Tok.Required));
    }

    // ── event context: an instant in the report body ──

    [Theory]
    [InlineData(6, "6:00 AM")]      // culture form
    [InlineData(18, "6:00 PM")]     // culture form
    [InlineData(0, "12:00 AM")]     // event midnight = date-bound culture form, NOT a word
    public void EventClock_NonNoon_UsesCultureForm(int hour, string expected)
    {
        var t = StoreOmitting().ForLanguage("en");
        var local = new DateTime(2026, 6, 8, hour, 0, 0);
        Assert.Equal(expected, StructuredReportRenderer.FormatEventClock(local, local.ToString("h:mm tt", EnUs), t));
    }

    [Fact]
    public void EventClock_Noon_UsesBareNoonWord()
    {
        var t = StoreOmitting().ForLanguage("en");
        var noon = new DateTime(2026, 6, 8, 12, 0, 0);
        Assert.Equal("noon", StructuredReportRenderer.FormatEventClock(noon, noon.ToString("h:mm tt", EnUs), t));
    }

    [Fact]
    public void EventClock_Noon_Degrades_WhenNoonWordAbsent()  // soft fallback, never throws
    {
        var t = StoreOmitting(Tok.Noon, Tok.Midnight).ForLanguage("xx");
        var noon = new DateTime(2026, 6, 8, 12, 0, 0);
        Assert.Equal("12:00 PM", StructuredReportRenderer.FormatEventClock(noon, noon.ToString("h:mm tt", EnUs), t));
    }

    // ── schedule context: a recurring generation time stated precisely ──

    [Theory]
    [InlineData(6, "6:00 AM")]
    [InlineData(18, "6:00 PM")]
    [InlineData(12, "12:00 noon")]      // schedule noon = precise HH:MM + noon word
    [InlineData(0, "12:00 midnight")]   // schedule midnight = precise HH:MM + midnight word
    public void ScheduleClock_UsesPreciseTimePlusDesignator(int hour, string expected)
    {
        var t = StoreOmitting().ForLanguage("en");
        Assert.Equal(expected, StructuredReportRenderer.FormatScheduleClock(new DateTime(2026, 6, 8, hour, 0, 0), t, EnUs));
    }

    [Fact]
    public void ScheduleClock_Degrades_WhenWordsAbsent()  // soft fallback, never throws
    {
        var t = StoreOmitting(Tok.Noon, Tok.Midnight).ForLanguage("xx");
        Assert.Equal("12:00 PM", StructuredReportRenderer.FormatScheduleClock(new DateTime(2026, 6, 8, 12, 0, 0), t, EnUs));
        Assert.Equal("12:00 AM", StructuredReportRenderer.FormatScheduleClock(new DateTime(2026, 6, 8, 0, 0, 0), t, EnUs));
    }
}