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
public sealed class TopUpGenerationTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    // One in-memory DB per test (xUnit constructs a fresh instance per [Fact]); the connection is
    // held open for the DB's lifetime and closed in Dispose. Centralizes the SQLite bootstrap so
    // each test just uses _db.
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<WeatherDataContext> _db;

    public TopUpGenerationTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(_conn).Options;
        using var ctx = new WeatherDataContext(_db);
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
    }

    public void Dispose() => _conn.Dispose();

    // An en baseline row for a token (the ContextKind/ContextInfo source ApplyTopUp reads).
    private static LanguageTemplate BaseRow(string token, TemplateContextKind kind = TemplateContextKind.Example) => new()
    {
        Token = token,
        Phrase = $"en-{token}",
        ContextInfo = $"context for {token}",
        ContextKind = kind,
        Representable = true,
    };

    private static IReadOnlyDictionary<string, LanguageTemplate> Baseline(params string[] tokens) =>
        tokens.ToDictionary(t => t, t => BaseRow(t), StringComparer.Ordinal);

    private static TranslatedToken Tr(string token, string phrase, bool representable = true, string? note = null) =>
        new(token, representable ? phrase : "", $"ctx-{token}", representable, note);

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

    // Seeds an enabled language plus its existing template rows, returns its Id.
    private async Task<long> SeedLanguageAsync(string iso, DateTime? generatedAt, string? generationError,
        params LanguageTemplate[] rows)
    {
        await using var ctx = new WeatherDataContext(_db);
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

    [Fact]
    public async Task TopUp_FillsOnly_DoesNotOverwriteAPresentToken()
    {
        var langId = await SeedLanguageAsync("fr", T0, null, Row("A", "old-A"));

        IReadOnlyList<string> inserted;
        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // The translated set offers a NEW value for the present token A and a brand-new token B.
            inserted = await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("A", "new-A"), Tr("B", "bee") }, "fr-FR", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        Assert.Equal(new[] { "B" }, inserted);   // only the missing token was inserted
        await using (var ctx = new WeatherDataContext(_db))
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
        var reviewedAt = T0.AddDays(-1);
        var langId = await SeedLanguageAsync("sq", T0, null,
            Row("VisHazy", "Turbullt", reviewedBy: "paul", reviewedAt: reviewedAt));

        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // Top-up offers a re-translation of the reviewed token plus a new token.
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("VisHazy", "regenerated"), Tr("DayMon", "e hënë") },
                "sq-AL", Baseline("VisHazy", "DayMon"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(_db))
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
        // A language that was READY on the old baseline {A}; the baseline has since grown to {A, B}.
        var langId = await SeedLanguageAsync("es", T0, null, Row("A", "a-es"));

        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // Pass a DIFFERENT culture tag than the established "es-US" — a top-up must not adopt it.
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("B", "b-es") }, "es-ES", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(_db))
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
        long langId;
        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = new Language { IsoCode = "fr", DisplayName = "French", IsEnabled = true, CultureName = null };
            ctx.Languages.Add(lang);
            await ctx.SaveChangesAsync();
            langId = lang.Id;
        }

        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("A", "a-fr"), Tr("B", "b-fr") }, "fr-FR", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            Assert.Equal("fr-FR", lang.CultureName);           // culture IS set on first generation (was null)
            Assert.Equal(2, await ctx.LanguageTemplates.CountAsync(t => t.LanguageId == langId));
        }
    }

    [Fact]
    public async Task TopUp_CleanTopUp_KeepsPreExistingBlockedState()
    {
        // A language BLOCKED on token A (a present, not-representable row), now missing new token B.
        var langId = await SeedLanguageAsync("de", T0, "A cannot be expressed",
            Row("A", "", representable: false, note: "needs code"));

        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // A clean translation for B only — A is a present (blocked) row, not in the missing set.
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { Tr("B", "b-de") }, "de-DE", Baseline("A", "B"), T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(_db))
        {
            var b = await ctx.LanguageTemplates.SingleAsync(t => t.LanguageId == langId && t.Token == "B");
            Assert.Equal("b-de", b.Phrase);                    // the clean token was filled
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            Assert.NotNull(lang.GenerationError);              // re-stamped from the FULL set → still BLOCKED on A
            Assert.Contains("A", lang.GenerationError);
            Assert.Equal(LanguageGenerationState.Blocked, lang.GenerationState);
        }
    }

    [Fact]
    public async Task TopUp_HintContext_KeepsBaselineEnglishGloss()
    {
        var langId = await SeedLanguageAsync("de", T0, null);   // no rows yet
        // A baseline whose token carries a language-neutral English Hint gloss (read, not translated).
        var baseline = new Dictionary<string, LanguageTemplate>(StringComparer.Ordinal)
        {
            ["H"] = BaseRow("H", TemplateContextKind.Hint),
        };

        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = await ctx.Languages.SingleAsync(l => l.Id == langId);
            // Claude returns a TRANSLATED context; the Hint branch must ignore it and keep the baseline gloss.
            await ReportWorker.ApplyTopUpAsync(
                ctx, lang, new[] { new TranslatedToken("H", "de-phrase", "translated-context-de", true, null) },
                "de-DE", baseline, T0.AddMinutes(5), default);
        }

        await using (var ctx = new WeatherDataContext(_db))
        {
            var h = await ctx.LanguageTemplates.SingleAsync(t => t.LanguageId == langId && t.Token == "H");
            Assert.Equal("context for H", h.ContextInfo);      // baseline English gloss kept, not the translated context
            Assert.Equal(TemplateContextKind.Hint, h.ContextKind);
            Assert.Equal("de-phrase", h.Phrase);
        }
    }

    // ── WX-269: subscriber-aware generation-slot ordering (pure, no DB) ────────

    [Fact]
    public void OrderGenerationCandidates_SubscribedFirst_ThenTier_ThenMostSubscribed()
    {
        var items = new[]
        {
            (iso: "da", state: LanguageGenerationState.Ready,   missing: 1, hard: true,  subs: 0),   // suppressing BUT no subscribers
            (iso: "de", state: LanguageGenerationState.Ready,   missing: 1, hard: true,  subs: 3),    // suppressing, 3 subscribers
            (iso: "es", state: LanguageGenerationState.Ready,   missing: 1, hard: true,  subs: 10),   // suppressing, 10 subscribers
            (iso: "eo", state: LanguageGenerationState.Pending, missing: 5, hard: true,  subs: 2),    // fresh enable, 2 subscribers
        };

        var ordered = ReportWorker.OrderGenerationCandidates(
            items, w => w.state, w => w.missing, w => w.hard, w => w.subs, w => w.iso);

        // Subscribed languages first — `da` sorts LAST despite its tier-0 "suppressing" state, because
        // with zero recipients it suppresses nothing. Among the subscribed, the urgency tier dominates
        // (Ready-hard before Pending), then most-subscribed first (es > de).
        Assert.Equal(new[] { "es", "de", "eo", "da" }, ordered.Select(w => w.iso).ToArray());
    }

    [Fact]
    public void OrderGenerationCandidates_HardSuppression_OutranksHigherSubscribedSoftOnly()
    {
        var items = new[]
        {
            (iso: "de", state: LanguageGenerationState.Ready, missing: 1, hard: true,  subs: 2),    // hard-suppressing, 2 subscribers
            (iso: "es", state: LanguageGenerationState.Ready, missing: 1, hard: false, subs: 50),   // soft-only, 50 subscribers
        };

        var ordered = ReportWorker.OrderGenerationCandidates(
            items, w => w.state, w => w.missing, w => w.hard, w => w.subs, w => w.iso);

        // Both subscribed, so the urgency tier still governs: the hard-suppressing language comes first
        // even though the soft-only one has far more subscribers (subscriber count is the WITHIN-tier
        // tie-break, not a tier override). Settles the "should a high-subscriber soft-only jump a
        // hard-suppressing subscribed one?" question — no.
        Assert.Equal(new[] { "de", "es" }, ordered.Select(w => w.iso).ToArray());
    }

    [Fact]
    public void OrderGenerationCandidates_EqualTierAndSubscribers_FallsBackToIso()
    {
        // The common real case: a new baseline token leaves several languages missing it in the SAME
        // cycle with equal subscriber counts — the iso tie-break must decide deterministically.
        var items = new[]
        {
            (iso: "es", state: LanguageGenerationState.Ready, missing: 1, hard: true, subs: 4),
            (iso: "de", state: LanguageGenerationState.Ready, missing: 1, hard: true, subs: 4),
            (iso: "da", state: LanguageGenerationState.Ready, missing: 1, hard: true, subs: 4),
        };

        var ordered = ReportWorker.OrderGenerationCandidates(
            items, w => w.state, w => w.missing, w => w.hard, w => w.subs, w => w.iso);

        // Same subscribed tier + same subscriber count → alphabetical iso.
        Assert.Equal(new[] { "da", "de", "es" }, ordered.Select(w => w.iso).ToArray());
    }
}