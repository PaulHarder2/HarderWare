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
///
/// <para>Post-seed data migrations that <b>UPDATE</b> a template phrase are applied on top, so
/// the store mirrors the live DB state (seed + migrations) and the goldens verify the SHIPPED
/// copy rather than the original seed. WX-184's <c>RelabelCurrentToReportedConditions</c> is the
/// first such migration; its new phrases are read from the migration's <c>Up()</c> here, never
/// duplicated, so the same no-drift guarantee holds for the relabel.</para>
/// </summary>
public static class SeedTemplateStore
{
    // The WX-166 ISO-seed ids + cultures the migration sets for en/es (also fail-closed-guarded there).
    private static readonly Language En = new() { Id = 37, IsoCode = "en", DisplayName = "English", CultureName = "en-US" };
    private static readonly Language Es = new() { Id = 39, IsoCode = "es", DisplayName = "Spanish", CultureName = "es-US" };

    /// <summary>A store loaded with the migration's en/es template rows (seed + post-seed relabels).</summary>
    public static LanguageTemplateStore Build()
    {
        var rows = SeedRows();
        return new LanguageTemplateStore(() => rows);
    }

    /// <summary>The en + es <see cref="LanguageTemplate"/> rows: the seed with post-seed phrase relabels applied.</summary>
    public static IReadOnlyList<LanguageTemplate> SeedRows()
    {
        var src = MigrationSource();
        var relabels = ParseRelabels();
        var rows = new List<LanguageTemplate>();
        rows.AddRange(RowsFor(En, src, relabels));
        rows.AddRange(RowsFor(Es, src, relabels));
        return rows;
    }

    // Each seed row is `{ <id>L, "Token", "Phrase", "ContextInfo", "ContextKind", true }`. No seed
    // phrase contains a double-quote, so a non-escaping "([^"]*)" per field is sufficient (and the
    // same shape TokSeedParityTests relies on). Capture Token, Phrase, and the Representable bool.
    private static IEnumerable<LanguageTemplate> RowsFor(
        Language lang, string migration, IReadOnlyDictionary<(string Iso, string Token), string> relabels)
    {
        var rx = new Regex("\\{\\s*" + lang.Id + "L,\\s*\"([^\"]*)\",\\s*\"([^\"]*)\",\\s*\"[^\"]*\",\\s*\"[^\"]*\",\\s*(true|false)\\s*\\}");
        foreach (Match m in rx.Matches(migration))
        {
            var token = m.Groups[1].Value;
            // A later relabel migration wins over the seed phrase, so the store mirrors production.
            var phrase = relabels.TryGetValue((lang.IsoCode, token), out var relabeled)
                ? relabeled
                : m.Groups[2].Value;
            yield return new LanguageTemplate
            {
                LanguageId = lang.Id,
                Language = lang,
                Token = token,
                Phrase = phrase,
                Representable = bool.Parse(m.Groups[3].Value),
            };
        }
    }

    // (iso, token) -> relabeled phrase, read from the Up() of the post-seed relabel migration
    // (WX-184). Parsed from source so the values can't drift from what ships. The migration's
    // Relabel(...) helper rewrites the CurrentConditionsHeading token, with args (iso, phrase).
    private static IReadOnlyDictionary<(string Iso, string Token), string> ParseRelabels()
    {
        var map = new Dictionary<(string, string), string>();
        var up = UpBody(RelabelMigrationSource());
        var rx = new Regex("Relabel\\(migrationBuilder,\\s*\"([a-z]{2})\",\\s*\"([^\"]*)\"\\)");
        foreach (Match m in rx.Matches(up))
            map[(m.Groups[1].Value, "CurrentConditionsHeading")] = m.Groups[2].Value;
        return map;
    }

    // The Up() body only, so the Down() (revert) phrases are never read as overrides.
    private static string UpBody(string src)
    {
        var start = src.IndexOf("void Up(", StringComparison.Ordinal);
        var end = src.IndexOf("void Down(", StringComparison.Ordinal);
        return start >= 0 && end > start ? src[start..end] : src;
    }

    private static string MigrationSource([CallerFilePath] string thisFile = "") =>
        ReadSingleMigration("*_AddLanguageTemplates.cs", thisFile);

    private static string RelabelMigrationSource([CallerFilePath] string thisFile = "") =>
        ReadSingleMigration("*_RelabelCurrentToReportedConditions.cs", thisFile);

    private static string ReadSingleMigration(string glob, string thisFile)
    {
        var wxServices = Directory.GetParent(thisFile)!.Parent!.Parent!.FullName;
        var migDir = Path.Combine(wxServices, "src", "MetarParser.Data", "Migrations");
        var matches = Directory.GetFiles(migDir, glob)
            .Where(p => !p.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Expected exactly one {glob} migration in {migDir}, found {matches.Count}.");
        return File.ReadAllText(matches[0]);
    }
}