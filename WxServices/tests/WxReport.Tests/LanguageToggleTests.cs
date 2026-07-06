using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-249 — the non-destructive language enable/disable seam (<see cref="LanguageToggle"/>),
/// exercised against a real relational store (SQLite in-memory) so the durability of curated rows
/// across a disable → re-enable is proven end-to-end. The disable logic used to live in WxManager's
/// WPF code-behind (excluded from CI); extracting it into this seam is what makes the regression
/// testable. Mirrors the TopUpGenerationTests harness (remap nvarchar(max) → TEXT; hold the
/// connection open for the DB's lifetime).
/// </summary>
public sealed class LanguageToggleTests : IDisposable
{
    private static readonly DateTime Gen = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Reviewed = new(2026, 7, 4, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Gen2 = new(2026, 7, 4, 14, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<WeatherDataContext> _db;

    public LanguageToggleTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(_conn).Options;
        using var ctx = new WeatherDataContext(_db);
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
    }

    public void Dispose() => _conn.Dispose();

    // Seed one enabled, READY language carrying a single human-REVIEWED template row (a WX-244-class
    // curated QA edit — the precise thing a destructive disable would incinerate). Returns its Id.
    private async Task<long> SeedReviewedLanguageAsync(string iso = "sq", string reviewer = "paul")
    {
        await using var ctx = new WeatherDataContext(_db);
        var lang = new Language
        {
            IsoCode = iso,
            DisplayName = iso.ToUpperInvariant(),
            IsEnabled = true,
            CultureName = $"{iso}-{iso.ToUpperInvariant()}",
            GeneratedAtUtc = Gen,
        };
        ctx.Languages.Add(lang);
        await ctx.SaveChangesAsync();
        ctx.LanguageTemplates.Add(new LanguageTemplate
        {
            LanguageId = lang.Id,
            Token = "vis_hazy",
            Phrase = "Turbullt",
            ContextInfo = "ctx",
            ContextKind = TemplateContextKind.Example,
            Representable = true,
            ReviewedBy = reviewer,
            ReviewedAtUtc = Reviewed,
        });
        await ctx.SaveChangesAsync();
        return lang.Id;
    }

    [Fact]
    public async Task Disable_KeepsCuratedRowsAndReviewStamps()
    {
        var id = await SeedReviewedLanguageAsync();

        LanguageToggleResult result;
        await using (var ctx = new WeatherDataContext(_db))
            result = await LanguageToggle.SetEnabledAsync(ctx, id, enabled: false);

        Assert.Equal(LanguageToggleOutcome.Disabled, result.Outcome);

        await using var check = new WeatherDataContext(_db);
        var lang = await check.Languages.FindAsync(id);
        Assert.False(lang!.IsEnabled);
        Assert.Equal(Gen, lang.GeneratedAtUtc);   // stamp preserved — no revert to needs-generation
        var row = await check.LanguageTemplates.SingleAsync(t => t.LanguageId == id);
        Assert.Equal("Turbullt", row.Phrase);      // the reviewed row survived the disable
        Assert.Equal("paul", row.ReviewedBy);
        Assert.Equal(Reviewed, row.ReviewedAtUtc);
    }

    [Fact]
    public async Task DisableThenReEnable_ReusesRows_ReviewSurvives()
    {
        var id = await SeedReviewedLanguageAsync();

        await using (var ctx = new WeatherDataContext(_db))
            await LanguageToggle.SetEnabledAsync(ctx, id, enabled: false);

        LanguageToggleResult reenable;
        await using (var ctx = new WeatherDataContext(_db))
            reenable = await LanguageToggle.SetEnabledAsync(ctx, id, enabled: true);

        // Kept its GeneratedAtUtc through the disable, so re-enable finds it READY — no regeneration.
        Assert.Equal(LanguageToggleOutcome.Enabled, reenable.Outcome);

        await using var check = new WeatherDataContext(_db);
        var lang = await check.Languages.FindAsync(id);
        Assert.True(lang!.IsEnabled);
        Assert.True(lang.IsReady);
        var row = Assert.Single(await check.LanguageTemplates.Where(t => t.LanguageId == id).ToListAsync());
        Assert.Equal("Turbullt", row.Phrase);
        Assert.Equal("paul", row.ReviewedBy);      // human QA survived the full disable → re-enable round-trip
        Assert.Equal(Reviewed, row.ReviewedAtUtc);
    }

    [Fact]
    public async Task Disable_RefusedWhenRecipientsAssigned()
    {
        var id = await SeedReviewedLanguageAsync();
        await using (var ctx = new WeatherDataContext(_db))
        {
            ctx.Recipients.Add(new Recipient { RecipientId = "r1", Email = "r@x.com", Name = "R", LanguageId = id });
            await ctx.SaveChangesAsync();
        }

        LanguageToggleResult result;
        await using (var ctx = new WeatherDataContext(_db))
            result = await LanguageToggle.SetEnabledAsync(ctx, id, enabled: false);

        Assert.Equal(LanguageToggleOutcome.BlockedByRecipients, result.Outcome);
        Assert.Equal(1, result.AssignedRecipients);

        await using var check = new WeatherDataContext(_db);
        Assert.True((await check.Languages.FindAsync(id))!.IsEnabled);   // refusal, not a partial disable
    }

    [Fact]
    public async Task Enable_ClearsStaleGenerationError_Requeues()
    {
        // A disabled language hand-left in a BLOCKED-looking state (error + stamp).
        long id;
        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = new Language
            {
                IsoCode = "eo",
                DisplayName = "EO",
                IsEnabled = false,
                GeneratedAtUtc = Gen,
                GenerationError = "blocked: token X",
            };
            ctx.Languages.Add(lang);
            await ctx.SaveChangesAsync();
            id = lang.Id;
        }

        LanguageToggleResult result;
        await using (var ctx = new WeatherDataContext(_db))
            result = await LanguageToggle.SetEnabledAsync(ctx, id, enabled: true);

        Assert.Equal(LanguageToggleOutcome.EnabledWillGenerate, result.Outcome);
        await using var check = new WeatherDataContext(_db);
        var reloaded = await check.Languages.FindAsync(id);
        Assert.True(reloaded!.IsEnabled);
        Assert.Null(reloaded.GenerationError);   // stale error cleared
        Assert.Null(reloaded.GeneratedAtUtc);    // requeued to PENDING for the next cycle
    }

    [Fact]
    public async Task Enable_BlockedLanguage_DeletesBlockedRows_KeepsRepresentable()
    {
        // WX-253: a language disabled by block->disable carries a blocked PLACEHOLDER row (X,
        // Representable=false) alongside a real value (Y — reviewed, or supplied by hand while
        // disabled). Re-enabling must delete X so the token becomes no-row and the WX-250 auto-scan
        // re-attempts it as fair game, while preserving Y untouched.
        long id;
        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = new Language
            {
                IsoCode = "de",
                DisplayName = "DE",
                IsEnabled = false,
                CultureName = "de-DE",
                GeneratedAtUtc = Gen,
                GenerationError = "1 token(s) cannot be expressed in DE by simple substitution: X.",
            };
            ctx.Languages.Add(lang);
            await ctx.SaveChangesAsync();
            id = lang.Id;
            ctx.LanguageTemplates.AddRange(
                new LanguageTemplate
                {
                    LanguageId = id, Token = "X", Phrase = "",
                    ContextInfo = "ctx", ContextKind = TemplateContextKind.Example,
                    Representable = false, Note = "needs a code change",
                },
                new LanguageTemplate
                {
                    LanguageId = id, Token = "Y", Phrase = "y-de",
                    ContextInfo = "ctx", ContextKind = TemplateContextKind.Example,
                    Representable = true, ReviewedBy = "paul", ReviewedAtUtc = Reviewed,
                });
            await ctx.SaveChangesAsync();
        }

        LanguageToggleResult result;
        await using (var ctx = new WeatherDataContext(_db))
            result = await LanguageToggle.SetEnabledAsync(ctx, id, enabled: true);

        Assert.Equal(LanguageToggleOutcome.EnabledWillGenerate, result.Outcome);
        await using var check = new WeatherDataContext(_db);
        var reloaded = await check.Languages.FindAsync(id);
        Assert.True(reloaded!.IsEnabled);
        Assert.Null(reloaded.GenerationError);   // requeued to PENDING
        Assert.Null(reloaded.GeneratedAtUtc);
        // The blocked placeholder is gone (no-row → auto-scan re-attempts it); the real row survives.
        Assert.False(await check.LanguageTemplates.AnyAsync(t => t.LanguageId == id && t.Token == "X"));
        var y = await check.LanguageTemplates.SingleAsync(t => t.LanguageId == id && t.Token == "Y");
        Assert.Equal("y-de", y.Phrase);
        Assert.Equal("paul", y.ReviewedBy);
    }

    [Fact]
    public async Task Enable_BlockedLanguage_PreservesHumanReviewedBlockedRow()
    {
        // WX-253 regression guard: an operator can type a value into a blocked token via the Vocabulary
        // tab — that STAMPS ReviewedBy but leaves Representable read-only/false (the token still can't be
        // expressed by simple substitution). The re-enable's blocked-row purge must NOT destroy that
        // human-reviewed row (WX-249 durability): only Claude's own never-reviewed empty placeholders go.
        long id;
        await using (var ctx = new WeatherDataContext(_db))
        {
            var lang = new Language
            {
                IsoCode = "de",
                DisplayName = "DE",
                IsEnabled = false,
                CultureName = "de-DE",
                GeneratedAtUtc = Gen,
                GenerationError = "1 token(s) cannot be expressed in DE by simple substitution: R.",
            };
            ctx.Languages.Add(lang);
            await ctx.SaveChangesAsync();
            id = lang.Id;
            ctx.LanguageTemplates.AddRange(
                // P: Claude's own blocked placeholder — not representable, never reviewed → purged.
                new LanguageTemplate
                {
                    LanguageId = id, Token = "P", Phrase = "",
                    ContextInfo = "ctx", ContextKind = TemplateContextKind.Example,
                    Representable = false,
                },
                // R: a human-typed value on the same still-non-representable token → MUST survive.
                new LanguageTemplate
                {
                    LanguageId = id, Token = "R", Phrase = "hand-typed de",
                    ContextInfo = "ctx", ContextKind = TemplateContextKind.Example,
                    Representable = false, ReviewedBy = "paul", ReviewedAtUtc = Reviewed,
                });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new WeatherDataContext(_db))
            await LanguageToggle.SetEnabledAsync(ctx, id, enabled: true);

        await using var check = new WeatherDataContext(_db);
        Assert.False(await check.LanguageTemplates.AnyAsync(t => t.LanguageId == id && t.Token == "P")); // unreviewed placeholder purged
        var r = await check.LanguageTemplates.SingleAsync(t => t.LanguageId == id && t.Token == "R");
        Assert.Equal("hand-typed de", r.Phrase);        // human content preserved
        Assert.Equal("paul", r.ReviewedBy);
        Assert.False(r.Representable);                    // still non-representable — will re-block next cycle, not vanish
    }

    [Fact]
    public async Task SetEnabled_NotFound_WhenRowMissing()
    {
        await using var ctx = new WeatherDataContext(_db);
        var result = await LanguageToggle.SetEnabledAsync(ctx, languageId: 999, enabled: false);
        Assert.Equal(LanguageToggleOutcome.NotFound, result.Outcome);
    }

    // WX-249 generator-path AC: a language disabled DURING a slow generation call (a stale tracked
    // entity in the worker's context) must keep the freshly-generated rows AND stay disabled — the
    // WX-222 mid-call discard is retired, and the top-up stamp never writes IsEnabled, so EF's
    // column-scoped UPDATE leaves the concurrent disable intact. Locks the reasoning the removed
    // race-guard used to embody so a future stamp that touches IsEnabled (or a concurrency token)
    // would trip this test.
    [Fact]
    public async Task TopUp_ConcurrentlyDisabledMidCall_PersistsRowsButStaysDisabled()
    {
        long id;
        await using (var ctx = new WeatherDataContext(_db))
        {
            // Enabled + PENDING (no GeneratedAtUtc) — the state the worker generates from.
            var lang = new Language { IsoCode = "de", DisplayName = "German", IsEnabled = true, CultureName = "de-DE" };
            ctx.Languages.Add(lang);
            await ctx.SaveChangesAsync();
            id = lang.Id;
        }

        // Context A: the worker loads the language tracked at cycle start (still enabled).
        await using var ctxA = new WeatherDataContext(_db);
        var staleLang = await ctxA.Languages.FirstAsync(l => l.Id == id);

        // Context B: the operator disables it mid-call (during the slow Claude generation).
        await using (var ctxB = new WeatherDataContext(_db))
            await LanguageToggle.SetEnabledAsync(ctxB, id, enabled: false);

        // Context A persists the generated rows against its now-stale (still-enabled-in-memory) entity.
        var baseline = new Dictionary<string, LanguageTemplate>(StringComparer.Ordinal)
        {
            ["noon"] = new LanguageTemplate
            {
                Token = "noon",
                Phrase = "en-noon",
                ContextInfo = "ctx",
                ContextKind = TemplateContextKind.Example,
                Representable = true,
            },
        };
        var translations = new List<TranslatedToken> { new("noon", "Mittag", "ctx-noon", true, null) };
        await ReportWorker.ApplyTopUpAsync(ctxA, staleLang, translations, "de-DE", baseline, Gen2, default);

        await using var check = new WeatherDataContext(_db);
        var reloaded = await check.Languages.FindAsync(id);
        Assert.False(reloaded!.IsEnabled);                 // the concurrent disable SURVIVED the stamp
        Assert.Equal(Gen2, reloaded.GeneratedAtUtc);        // ...and the stamp still landed (dormant AND generated)
        Assert.True(await check.LanguageTemplates.AnyAsync(t => t.LanguageId == id && t.Token == "noon"));
    }
}