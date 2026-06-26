using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using MetarParser.Data.Entities;

using WxReport.Svc;

namespace WxReport.Tests;

/// <summary>
/// Builds a <see cref="LanguageTemplateStore"/> from the migrations' en/es seed — the SAME rows
/// production loads from the database — so tests render and gate against the real localized
/// phrases without a database (WX-171). The phrases are parsed out of the migration source (as
/// <c>TokSeedParityTests</c> relies on) rather than duplicated, so the test store and the shipped
/// seed can never silently drift.
///
/// <para>It scans EVERY migration, so the store mirrors the live DB state (seed + later inserts +
/// relabels): any <c>InsertData</c> into <c>LanguageTemplates</c> (the WX-171 seed, the WX-223
/// meteogram tokens, …) is picked up by the same row regex, and any post-seed phrase relabel
/// (WX-184, WX-224) is applied on top from that migration's <c>Up()</c>. A new InsertData
/// vocabulary migration needs no change here; a relabel migration also needs none as long as it
/// uses one of the Relabel() shapes <see cref="ParseRelabels"/> parses (the WX-184 3-arg form, or
/// the WX-224 4-arg form that names the token). Only InsertData rows / Relabel() calls match the
/// patterns, so scanning every file is safe.</para>
/// </summary>
public static class SeedTemplateStore
{
    // The WX-166 ISO-seed ids + cultures the migration sets for en/es (also fail-closed-guarded there).
    private static readonly Language En = new() { Id = 37, IsoCode = "en", DisplayName = "English", CultureName = "en-US" };
    private static readonly Language Es = new() { Id = 39, IsoCode = "es", DisplayName = "Spanish", CultureName = "es-US" };

    /// <summary>A store loaded with the migrations' en/es template rows (seed + later inserts + relabels).</summary>
    public static LanguageTemplateStore Build()
    {
        var rows = SeedRows();
        return new LanguageTemplateStore(() => rows);
    }

    /// <summary>The en + es <see cref="LanguageTemplate"/> rows: every migration's seed inserts with post-seed relabels applied.</summary>
    public static IReadOnlyList<LanguageTemplate> SeedRows()
    {
        var migrations = MigrationSources();
        var relabels = ParseRelabels(migrations);
        var rows = new List<LanguageTemplate>();
        foreach (var src in migrations)
        {
            // Up() only — a Down() that reverts by re-inserting rows must not be read as live seed.
            var up = UpBody(src);
            rows.AddRange(RowsFor(En, up, relabels));
            rows.AddRange(RowsFor(Es, up, relabels));
        }
        if (rows.Count == 0)
            throw new InvalidOperationException(
                "SeedTemplateStore parsed no LanguageTemplates seed rows — a seed migration was moved, " +
                "renamed, or changed format. Check src/MetarParser.Data/Migrations.");
        return rows;
    }

    // Each seed row is `{ <id>L, "Token", "Phrase", "ContextInfo", "ContextKind", true }`. No seed
    // phrase contains a double-quote, so a non-escaping "([^"]*)" per field is sufficient (and the
    // same shape TokSeedParityTests relies on). Only InsertData LanguageTemplates rows match this
    // shape, so scanning every migration file is safe. Capture Token, Phrase, and the Representable bool.
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

    // (iso, token) -> relabeled phrase, read from the Up() of any post-seed relabel migration so
    // the test store mirrors the live DB (the values can't drift from what ships). Two helper
    // shapes are parsed: the WX-184 3-arg Relabel(mb, iso, phrase) — implicitly the
    // CurrentConditionsHeading token — and the WX-224 4-arg Relabel(mb, iso, token, phrase), which
    // names the token explicitly (e.g. the MeteogramCaption gloss relabel). Param-name-agnostic, so
    // either migration's local helper name matches.
    private static IReadOnlyDictionary<(string Iso, string Token), string> ParseRelabels(IEnumerable<string> migrations)
    {
        var map = new Dictionary<(string, string), string>();
        // 4-arg: Relabel(_, "xx", "TokenName", "phrase"); token is letters/underscore.
        var rx4 = new Regex("Relabel\\(\\w+,\\s*\"([a-z]{2})\",\\s*\"([A-Za-z_]+)\",\\s*\"([^\"]*)\"\\s*\\)");
        // 3-arg (WX-184): Relabel(_, "xx", "phrase") -> CurrentConditionsHeading. The ")" right after
        // the 2nd string keeps this from also matching a 4-arg call.
        var rx3 = new Regex("Relabel\\(\\w+,\\s*\"([a-z]{2})\",\\s*\"([^\"]*)\"\\s*\\)");
        foreach (var src in migrations)
        {
            var up = UpBody(src);
            foreach (Match m in rx4.Matches(up))
                map[(m.Groups[1].Value, m.Groups[2].Value)] = m.Groups[3].Value;
            foreach (Match m in rx3.Matches(up))
                map[(m.Groups[1].Value, "CurrentConditionsHeading")] = m.Groups[2].Value;
        }
        return map;
    }

    // The Up() body only, so a Down() (revert) Relabel is never read as an override.
    private static string UpBody(string src)
    {
        var start = src.IndexOf("void Up(", StringComparison.Ordinal);
        var end = src.IndexOf("void Down(", StringComparison.Ordinal);
        return start >= 0 && end > start ? src[start..end] : src;
    }

    // Every migration file's source (excluding the EF Designer and model-snapshot files).
    private static IReadOnlyList<string> MigrationSources([CallerFilePath] string thisFile = "")
    {
        var wxServices = Directory.GetParent(thisFile)!.Parent!.Parent!.FullName;
        var migDir = Path.Combine(wxServices, "src", "MetarParser.Data", "Migrations");
        return Directory.GetFiles(migDir, "*.cs")
            .Where(p => !p.EndsWith(".Designer.cs", StringComparison.Ordinal)
                     && !p.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToList();
    }
}