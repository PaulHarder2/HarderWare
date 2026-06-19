using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using MetarParser.Data.Entities;

using WxReport.Svc;

namespace WxReport.Tests;

/// <summary>
/// Builds a <see cref="LanguageTemplateStore"/> from the AddLanguageTemplates migration's
/// en/es seed — the SAME rows production loads from the database — so tests render and gate
/// against the real localized phrases without a database (WX-171). The seed is parsed out of
/// the migration source (as <c>TokSeedParityTests</c> does) rather than duplicated, so the
/// test store and the shipped seed can never silently drift.
/// </summary>
public static class SeedTemplateStore
{
    // The WX-166 ISO-seed ids + cultures the migration sets for en/es (also fail-closed-guarded there).
    private static readonly Language En = new() { Id = 37, IsoCode = "en", DisplayName = "English", CultureName = "en-US" };
    private static readonly Language Es = new() { Id = 39, IsoCode = "es", DisplayName = "Spanish", CultureName = "es-US" };

    /// <summary>A store loaded with the migration's en/es template rows.</summary>
    public static LanguageTemplateStore Build()
    {
        var rows = SeedRows();
        return new LanguageTemplateStore(() => rows);
    }

    /// <summary>The en + es <see cref="LanguageTemplate"/> rows parsed from the migration seed.</summary>
    public static IReadOnlyList<LanguageTemplate> SeedRows()
    {
        var src = MigrationSource();
        var rows = new List<LanguageTemplate>();
        rows.AddRange(RowsFor(En, src));
        rows.AddRange(RowsFor(Es, src));
        return rows;
    }

    // Each seed row is `{ <id>L, "Token", "Phrase", "ContextInfo", "ContextKind", true }`. No seed
    // phrase contains a double-quote, so a non-escaping "([^"]*)" per field is sufficient (and the
    // same shape TokSeedParityTests relies on). Capture Token, Phrase, and the Representable bool.
    private static IEnumerable<LanguageTemplate> RowsFor(Language lang, string migration)
    {
        var rx = new Regex("\\{\\s*" + lang.Id + "L,\\s*\"([^\"]*)\",\\s*\"([^\"]*)\",\\s*\"[^\"]*\",\\s*\"[^\"]*\",\\s*(true|false)\\s*\\}");
        foreach (Match m in rx.Matches(migration))
            yield return new LanguageTemplate
            {
                LanguageId = lang.Id,
                Language = lang,
                Token = m.Groups[1].Value,
                Phrase = m.Groups[2].Value,
                Representable = bool.Parse(m.Groups[3].Value),
            };
    }

    private static string MigrationSource([CallerFilePath] string thisFile = "")
    {
        var wxServices = Directory.GetParent(thisFile)!.Parent!.Parent!.FullName;
        var migDir = Path.Combine(wxServices, "src", "MetarParser.Data", "Migrations");
        var matches = Directory.GetFiles(migDir, "*_AddLanguageTemplates.cs")
            .Where(p => !p.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Expected exactly one AddLanguageTemplates migration in {migDir}, found {matches.Count}.");
        return File.ReadAllText(matches[0]);
    }
}