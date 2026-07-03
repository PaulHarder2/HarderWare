using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-250 — the fill-only top-up DB logic behind <see cref="ReportWorker.ApplyTopUpAsync"/>,
/// exercised against a real relational store (SQLite in-memory) so the unique index
/// <c>UX_LanguageTemplates_LanguageId_Token</c> enforces the never-clobber guarantee for real.
/// Mirrors the QaRerunStoreTests harness (remap nvarchar(max) → TEXT; keep the connection open
/// for the DB's lifetime). No Claude call is needed: the translated set is supplied directly,
/// which is exactly why the DB mutation was extracted into its own seam.
/// </summary>
public class TopUpGenerationTests
{
    private static readonly DateTime T0 = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<WeatherDataContext> NewDb(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
        return options;
    }

    // An en baseline row for a token (the ContextKind/ContextInfo source ApplyTopUp reads).
    private static LanguageTemplate BaseRow(string token) => new()
    {
        Token = token,
        Phrase = $"en-{token}",
        ContextInfo = $"context for {token}",
        ContextKind = TemplateContextKind.Example,
        Representable = true,
    };

    private static IReadOnlyDictionary<string, LanguageTemplate> Baseline(params string[] tokens) =>
        tokens.ToDictionary(t => t, BaseRow, StringComparer.Ordinal);

    private static TranslatedToken Tr(string token, string phrase, bool representable = true, string? note = null) =>
        new(token, representable ? phrase : "", $"ctx-{token}", representable, note);

    // Seeds an enabled language plus its existing template rows, returns its Id.
    private static async Task<long> SeedLanguageAsync(
        DbContextOptions<WeatherDataContext> db, string iso, DateTime? generatedAt, string? generationError,
        params LanguageTemplate[] rows)
    {
        await using var ctx = new WeatherDataContext(db);
        var lang = new Language
        {
            IsoCode = iso,
            DisplayName = iso.ToUpperInvariant(),
            IsEnabled = true,
            CultureName = $"{iso}-US",
            GeneratedAtUtc = generatedAt,
            GenerationError = generationError,
        };
        ctx.Languages.Add(lang);
        await ctx.SaveChangesAsync();
        foreach (var r in rows) r.LanguageId = lang.Id;
        ctx.LanguageTemplates.AddRange(rows);
        await ctx.SaveChangesAsync();
        return lang.Id;
    }

    private static LanguageTemplate Row(string token, string phrase, bool representable = true,
        string? reviewedBy = null, DateTime? reviewedAt = null, string? note = null) => new()
        {
            Token = token,
            Phrase = representable ? phrase : "",
            ContextInfo = $"ctx-{token}",
            ContextKind = TemplateContextKind.Example,
            Representable = representable,
            Note = note,
            ReviewedBy = reviewedBy,
            ReviewedAtUtc = reviewedAt,
        };

    [Fact]
    public async Task TopUp_FillsOnly_DoesNotOverwriteAPresentToken()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        var langId = await SeedLanguageAsync(db, "fr", T0, null, Row("A", "old-A"));

        IReadOnlyList<string> inserted;
        await using (var ctx = new WeatherDataContext(db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // The translated set offers a NEW value for the present token A and a brand-new token B.
            inserted = await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("A", "new-A"), Tr("B", "bee") }, "fr-FR", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        Assert.Equal(new[] { "B" }, inserted);   // only the missing token was inserted
        await using (var ctx = new WeatherDataContext(db))
        {
            var a = await ctx.LanguageTemplates.SingleAsync(t => t.LanguageId == langId && t.Token == "A");
            Assert.Equal("old-A", a.Phrase);      // present token untouched — never clobbered
            var b = await ctx.LanguageTemplates.SingleAsync(t => t.LanguageId == langId && t.Token == "B");
            Assert.Equal("bee", b.Phrase);
            Assert.Equal(2, await ctx.LanguageTemplates.CountAsync(t => t.LanguageId == langId)); // no duplicate A
        }
    }

    [Fact]
    public async Task TopUp_ReviewedRow_SurvivesUntouched()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        var reviewedAt = T0.AddDays(-1);
        var langId = await SeedLanguageAsync(db, "sq", T0, null,
            Row("VisHazy", "Turbullt", reviewedBy: "paul", reviewedAt: reviewedAt));

        await using (var ctx = new WeatherDataContext(db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // Top-up offers a re-translation of the reviewed token plus a new token.
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("VisHazy", "regenerated"), Tr("DayMon", "e hënë") },
                "sq-AL", Baseline("VisHazy", "DayMon"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(db))
        {
            var reviewed = await ctx.LanguageTemplates.SingleAsync(t => t.LanguageId == langId && t.Token == "VisHazy");
            Assert.Equal("Turbullt", reviewed.Phrase);        // curated value preserved
            Assert.Equal("paul", reviewed.ReviewedBy);        // review stamp preserved
            Assert.Equal(reviewedAt, reviewed.ReviewedAtUtc);
            var added = await ctx.LanguageTemplates.SingleAsync(t => t.LanguageId == langId && t.Token == "DayMon");
            Assert.Null(added.ReviewedBy);                    // freshly generated — unreviewed
        }
    }

    [Fact]
    public async Task TopUp_NewBaselineToken_PropagatesToAlreadyReadyLanguage_StaysReady()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        // A language that was READY on the old baseline {A}; the baseline has since grown to {A, B}.
        var langId = await SeedLanguageAsync(db, "es", T0, null, Row("A", "a-es"));

        await using (var ctx = new WeatherDataContext(db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // Pass a DIFFERENT culture tag than the established "es-US" — a top-up must not adopt it.
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("B", "b-es") }, "es-ES", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(db))
        {
            Assert.Equal(2, await ctx.LanguageTemplates.CountAsync(t => t.LanguageId == langId)); // B propagated
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            Assert.Equal(T0.AddMinutes(5), lang.GeneratedAtUtc);
            Assert.Null(lang.GenerationError);                 // still READY
            Assert.Equal(LanguageGenerationState.Ready, lang.GenerationState);
            Assert.Equal("es-US", lang.CultureName);           // established curated locale preserved, not re-derived
        }
    }

    [Fact]
    public async Task TopUp_FreshLanguage_SetsCultureWhenAbsent()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        long langId;
        await using (var ctx = new WeatherDataContext(db))
        {
            var lang = new Language { IsoCode = "fr", DisplayName = "French", IsEnabled = true, CultureName = null };
            ctx.Languages.Add(lang);
            await ctx.SaveChangesAsync();
            langId = lang.Id;
        }

        await using (var ctx = new WeatherDataContext(db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("A", "a-fr"), Tr("B", "b-fr") }, "fr-FR", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            Assert.Equal("fr-FR", lang.CultureName);           // culture IS set on first generation (was null)
            Assert.Equal(2, await ctx.LanguageTemplates.CountAsync(t => t.LanguageId == langId));
        }
    }

    [Fact]
    public async Task TopUp_CleanTopUp_KeepsPreExistingBlockedState()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        // A language BLOCKED on token A (a present, not-representable row), now missing new token B.
        var langId = await SeedLanguageAsync(db, "de", T0, "A cannot be expressed",
            Row("A", "", representable: false, note: "needs code"));

        await using (var ctx = new WeatherDataContext(db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // A clean translation for B only — A is a present (blocked) row, not in the missing set.
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("B", "b-de") }, "de-DE", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(db))
        {
            var b = await ctx.LanguageTemplates.SingleAsync(t => t.LanguageId == langId && t.Token == "B");
            Assert.Equal("b-de", b.Phrase);                    // the clean token was filled
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            Assert.NotNull(lang.GenerationError);              // re-stamped from the FULL set → still BLOCKED on A
            Assert.Contains("A", lang.GenerationError);
            Assert.Equal(LanguageGenerationState.Blocked, lang.GenerationState);
        }
    }
}