using WxReport.Svc.TranslationQa;

using Xunit;

namespace WxReport.Tests;

/// <summary>WX-217 — the judging-request assembler is pure; these lock its structure (preamble asks +
/// defined response shape, embedded reports, the paired-vocabulary JSON with blocked terms carried through).</summary>
public class JudgingPayloadTests
{
    private static RenderedScenario Warm() => new(
        "warm-convective",
        "summer cold front",
        "<div>Reported Conditions at Spring</div>",
        "<div>Gemeldete Wetterlage in Spring</div>");

    private static List<VocabularyPair> Vocab() =>
    [
        new("rain_light", "light rain", "Light rain was falling.", "Example", "leichter Regen", "Leichter Regen fiel.", Representable: true, Note: null, Reviewed: false),
        new("sev_storms_likely", "severe storms likely", "Severe storms are likely.", "Hint", "", "", Representable: false, Note: "German word order needs a restructuring the atomic renderer can't do", Reviewed: false),
    ];

    [Fact]
    public void Build_Includes_Preamble_Reports_And_VocabularyJson()
    {
        var md = JudgingPayload.Build("de", "German", [Warm()], Vocab());

        // Preamble: the four asks + the defined response shape + the advisory framing.
        Assert.Contains("judging request", md);
        Assert.Contains("Back-translate", md);
        Assert.Contains("selfReportedConfidence", md);
        Assert.Contains("vocabularyVerdicts", md);
        Assert.Contains("advisory", md);

        // Both rendered reports are embedded, labelled by scenario.
        Assert.Contains("warm-convective", md);
        Assert.Contains("Reported Conditions at Spring", md);
        Assert.Contains("Gemeldete Wetterlage in Spring", md);

        // Paired vocabulary as camelCase JSON, with the blocked token + its note carried through.
        Assert.Contains("\"token\": \"sev_storms_likely\"", md);
        Assert.Contains("\"representable\": false", md);
        Assert.Contains("German word order needs a restructuring", md);
    }

    [Fact]
    public void Build_Notes_Blocked_Term_Count_In_Preamble()
    {
        var md = JudgingPayload.Build("de", "German", [Warm()], Vocab());
        Assert.Contains("representable: false", md); // the preamble flags that blocked terms are present
    }

    [Fact]
    public void Build_AllRepresentable_OmitsBlockedNote()
    {
        var allOk = new List<VocabularyPair>
        {
            new("rain_light", "light rain", "Light rain was falling.", "Example", "leichter Regen", "Leichter Regen fiel.", Representable: true, Note: null, Reviewed: true),
        };
        var md = JudgingPayload.Build("de", "German", [Warm()], allOk);
        Assert.DoesNotContain("term(s) are marked", md); // no blocked-term sentence when all representable
    }
}