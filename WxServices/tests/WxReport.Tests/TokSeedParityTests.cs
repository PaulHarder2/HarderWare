using System.Reflection;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-171 build-time guard. The renderer's token contract (<see cref="Tok"/>) must
/// match the en/es seed in the AddLanguageTemplates migration EXACTLY: no token the
/// renderer can reference is unseeded, and no seeded token is orphaned. Drift fails
/// CI before it can reach the runtime completeness check. Baseline (en/es) only —
/// languages generated at runtime (WX-172) are verified by the runtime check, since
/// they do not exist at compile time. The seed is parsed once by
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
    public void Tok_matches_en_and_es_seed_exactly()
    {
        var tok = TokValues();
        var en = SeedTokens("en");
        var es = SeedTokens("es");

        Assert.True(en.SetEquals(es),
            $"en/es seed token sets differ. en-only: [{string.Join(", ", en.Except(es).OrderBy(x => x))}]; " +
            $"es-only: [{string.Join(", ", es.Except(en).OrderBy(x => x))}]");

        Assert.True(tok.SetEquals(en),
            $"Tok<->seed drift. In Tok but not seeded: [{string.Join(", ", tok.Except(en).OrderBy(x => x))}]; " +
            $"seeded but not in Tok: [{string.Join(", ", en.Except(tok).OrderBy(x => x))}]");
    }
}