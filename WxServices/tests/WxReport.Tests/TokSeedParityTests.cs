using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-171 build-time guard. The renderer's token contract (<see cref="Tok"/>) must
/// match the en/es seed in the AddLanguageTemplates migration EXACTLY: no token the
/// renderer can reference is unseeded, and no seeded token is orphaned. Drift fails
/// CI before it can reach the runtime completeness check. Baseline (en/es) only —
/// languages generated at runtime (WX-172) are verified by the runtime check, since
/// they do not exist at compile time.
/// </summary>
public class TokSeedParityTests
{
    private static IReadOnlySet<string> TokValues() =>
        typeof(Tok).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    // Anchored on this test file's compile-time path (as the golden harness does),
    // then up to WxServices/ and into the migration. The migration filename carries a
    // timestamp, so match by suffix; exactly one non-Designer file is expected.
    private static string MigrationSource([CallerFilePath] string thisFile = "")
    {
        var wxServices = Directory.GetParent(thisFile)!.Parent!.Parent!.FullName;
        var migDir = Path.Combine(wxServices, "src", "MetarParser.Data", "Migrations");
        var matches = Directory.GetFiles(migDir, "*_AddLanguageTemplates.cs")
            .Where(p => !p.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();
        Assert.True(matches.Count == 1,
            $"expected exactly one AddLanguageTemplates migration in {migDir}, found {matches.Count}");
        return File.ReadAllText(matches[0]);
    }

    private static IReadOnlySet<string> SeedTokens(long languageId, string migration)
    {
        var rx = new Regex("\\{\\s*" + languageId + "L,\\s*\"([^\"]+)\"");
        return rx.Matches(migration).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Tok_matches_en_and_es_seed_exactly()
    {
        var tok = TokValues();
        var migration = MigrationSource();
        var en = SeedTokens(37, migration);
        var es = SeedTokens(39, migration);

        Assert.True(en.SetEquals(es),
            $"en/es seed token sets differ. en-only: [{string.Join(", ", en.Except(es).OrderBy(x => x))}]; " +
            $"es-only: [{string.Join(", ", es.Except(en).OrderBy(x => x))}]");

        Assert.True(tok.SetEquals(en),
            $"Tok<->seed drift. In Tok but not seeded: [{string.Join(", ", tok.Except(en).OrderBy(x => x))}]; " +
            $"seeded but not in Tok: [{string.Join(", ", en.Except(tok).OrderBy(x => x))}]");
    }
}