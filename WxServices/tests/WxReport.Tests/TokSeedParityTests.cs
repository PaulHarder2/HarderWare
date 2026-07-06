using System.Reflection;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-171 build-time guard. The renderer's token contract (<see cref="Tok"/>) must
/// match the en seed in the AddLanguageTemplates migration EXACTLY: no token the
/// renderer can reference is unseeded, and no seeded token is orphaned. Drift fails
/// CI before it can reach the runtime completeness check. Baseline (en) only —
/// every target language is generated at runtime (WX-172/WX-250) and verified by the
/// runtime completeness check, since they do not exist at compile time. The seed is parsed once by
/// <see cref="SeedTemplateStore"/> (shared with the renderer/reconciler test store), so
/// the parity gate and the test renderer can never read the migration through diverging
/// parsers — a parser bug surfaces here as a parity failure rather than being masked.
/// </summary>
public class TokSeedParityTests
{
    private static IReadOnlySet<string> TokValues() =>
        typeof(Tok).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlySet<string> SeedTokens(string iso) =>
        SeedTemplateStore.SeedRows()
            .Where(r => r.Language?.IsoCode == iso)
            .Select(r => r.Token)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Tok_matches_en_seed_exactly()
    {
        var tok = TokValues();
        var en = SeedTokens("en");

        Assert.True(tok.SetEquals(en),
            $"Tok<->seed drift. In Tok but not seeded: [{string.Join(", ", tok.Except(en).OrderBy(x => x))}]; " +
            $"seeded but not in Tok: [{string.Join(", ", en.Except(tok).OrderBy(x => x))}]");
    }

    [Fact]
    public void Daypart_tokens_renamed_to_ordinals_and_DayPart1_seeded()   // WX-265
    {
        // Exercises the rename migration's data effect as SeedTemplateStore models it: the three
        // daypart words now carry the ordinal keys (phrases unchanged), the 00-06 token is seeded
        // ("early hours"), and the old Part* keys are gone. If the RenameToken parser or the
        // DayPart1 InsertData row regressed, this fails before the set-equality test does.
        var en = SeedTemplateStore.SeedRows()
            .Where(r => r.Language?.IsoCode == "en")
            .ToDictionary(r => r.Token, r => r.Phrase, StringComparer.Ordinal);

        Assert.Equal("Morning", en[Tok.DayPart2]);
        Assert.Equal("Afternoon", en[Tok.DayPart3]);
        Assert.Equal("Evening", en[Tok.DayPart4]);
        Assert.Equal("early hours", en[Tok.DayPart1]);

        Assert.DoesNotContain("PartMorning", en.Keys);
        Assert.DoesNotContain("PartAfternoon", en.Keys);
        Assert.DoesNotContain("PartEvening", en.Keys);
    }
}