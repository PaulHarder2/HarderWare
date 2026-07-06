using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using MetarParser.Data.Entities;

using WxReport.Svc;

namespace WxReport.Tests;

/// <summary>
/// Builds a <see cref="LanguageTemplateStore"/> from the migrations' en seed — the SAME rows
/// production loads from the database — so tests render and gate against the real localized
/// phrases without a database (WX-171). The phrases are parsed out of the migration source (as
/// <c>TokSeedParityTests</c> relies on) rather than duplicated, so the test store and the shipped
/// seed can never silently drift. WX-251 made the seed en-only; target languages are generated
/// at runtime (WX-250) and are not present here.
///
/// <para>It scans EVERY migration, so the store mirrors the live DB state (seed + later inserts +
/// relabels + token-key renames): any <c>InsertData</c> into <c>LanguageTemplates</c> (the WX-171
/// seed, the WX-223 meteogram tokens, …) is picked up by the same row regex; any post-seed phrase
/// relabel (WX-184, WX-224) is applied on top from that migration's <c>Up()</c>; and any post-seed
/// token-key rename (WX-265, e.g. <c>PartMorning→DayPart2</c>) is applied on top too, so the parsed
/// seed matches the renamed <c>Tok</c> contract without touching the historical seed migration. A
/// new InsertData vocabulary migration needs no change here; a relabel migration needs none as long
/// as it uses one of the Relabel() shapes <see cref="ParseRelabels"/> parses (the WX-184 3-arg form
/// or the WX-224 4-arg form that names the token); a rename migration needs none as long as it uses
/// the <c>RenameToken(mb, "old", "new")</c> shape <see cref="ParseRenames"/> parses. Only InsertData
/// rows / Relabel() / RenameToken() calls match the patterns, so scanning every file is safe.</para>
/// </summary>
public static class SeedTemplateStore
{
    // The WX-166 ISO-seed id + culture the migration sets for en (also fail-closed-guarded there).
    // WX-251: en is the only seeded language; target languages are generated at runtime (WX-250).
    private static readonly Language En = new() { Id = 37, IsoCode = "en", DisplayName = "English", CultureName = "en-US" };

    /// <summary>A store loaded with the migrations' en template rows (seed + later inserts + relabels).</summary>
    public static LanguageTemplateStore Build()
    {
        var rows = SeedRows();
        return new LanguageTemplateStore(() => rows);
    }

    /// <summary>The en <see cref="LanguageTemplate"/> rows: every migration's seed inserts with post-seed relabels applied.</summary>
    public static IReadOnlyList<LanguageTemplate> SeedRows()
    {
        var migrations = MigrationSources();
        var renames = ParseRenames(migrations);
        // Relabels are matched against the SEEDED (pre-rename) token name in RowsFor, but renames
        // apply afterward — so a relabel authored AFTER a rename (keyed on the new DayPart* name)
        // would miss the seed row. Mirror each such relabel back onto the old name so it still
        // lands; ApplyRenames then carries the relabeled phrase to the new key.
        var relabels = AliasRelabelsThroughRenames(ParseRelabels(migrations), renames);
        var rows = new List<LanguageTemplate>();
        foreach (var src in migrations)
        {
            // Up() only — a Down() that reverts by re-inserting rows must not be read as live seed.
            var up = UpBody(src);
            rows.AddRange(RowsFor(En, up, relabels));
        }
        // Token-key renames (WX-265) apply after all rows are collected, since a rename migration
        // renames rows seeded by an earlier migration; in migration order so any chain resolves.
        ApplyRenames(rows, renames);
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

    // Post-seed token-key renames (WX-265), read from the Up() of any rename migration so the test
    // store mirrors the live DB (the renamed keys can't drift from what ships). Parses the migration-
    // local helper shape RenameToken(mb, "OldToken", "NewToken"); tokens are letters/digits/underscore.
    // Order is preserved across migrations so a chained rename (A→B then B→C) resolves correctly.
    private static IReadOnlyList<(string Old, string New)> ParseRenames(IEnumerable<string> migrations)
    {
        var list = new List<(string, string)>();
        var rx = new Regex("RenameToken\\(\\w+,\\s*\"([A-Za-z0-9_]+)\",\\s*\"([A-Za-z0-9_]+)\"\\s*\\)");
        foreach (var src in migrations)
            foreach (Match m in rx.Matches(UpBody(src)))
                list.Add((m.Groups[1].Value, m.Groups[2].Value));
        return list;
    }

    // A relabel authored after a rename is keyed on the token's NEW name, but RowsFor matches the
    // seeded (pre-rename) name — so mirror each relabel back onto its seeded token name. The seed
    // row (old name) then picks up the relabeled phrase, and ApplyRenames carries it to the new key
    // — faithful whether the relabel precedes or follows the rename. Chain-transitive to match
    // ApplyRenames: a relabel keyed on C where the chain is A→B→C is aliased onto the root A.
    private static IReadOnlyDictionary<(string Iso, string Token), string> AliasRelabelsThroughRenames(
        IReadOnlyDictionary<(string Iso, string Token), string> relabels,
        IReadOnlyList<(string Old, string New)> renames)
    {
        if (renames.Count == 0 || relabels.Count == 0)
            return relabels;
        var map = new Dictionary<(string, string), string>();
        foreach (var kv in relabels)
            map[kv.Key] = kv.Value;
        foreach (var kv in relabels)
        {
            var seeded = SeededToken(kv.Key.Token, renames);
            if (!string.Equals(seeded, kv.Key.Token, StringComparison.Ordinal))
                map.TryAdd((kv.Key.Iso, seeded), kv.Value);
        }
        return map;
    }

    // Walk the rename chain backward (C←B←A) to the seeded (pre-rename) token name, so a relabel
    // keyed on any post-rename name resolves to the name RowsFor matches. Transitive, matching
    // ApplyRenames' in-order chain resolution; the counter bounds a pathological cycle.
    private static string SeededToken(string token, IReadOnlyList<(string Old, string New)> renames)
    {
        var current = token;
        for (int guard = renames.Count; guard >= 0; guard--)
        {
            var prior = current;
            for (int i = renames.Count - 1; i >= 0; i--)
                if (string.Equals(renames[i].New, current, StringComparison.Ordinal))
                {
                    current = renames[i].Old;
                    break;
                }
            if (string.Equals(current, prior, StringComparison.Ordinal))
                break;
        }
        return current;
    }

    // Apply the (old→new) token-key renames in migration order, mirroring what the rename migration
    // does to the live rows. A token with no matching row is a no-op (as in the DB).
    private static void ApplyRenames(List<LanguageTemplate> rows, IReadOnlyList<(string Old, string New)> renames)
    {
        foreach (var (oldToken, newToken) in renames)
            foreach (var row in rows)
                if (string.Equals(row.Token, oldToken, StringComparison.Ordinal))
                    row.Token = newToken;
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