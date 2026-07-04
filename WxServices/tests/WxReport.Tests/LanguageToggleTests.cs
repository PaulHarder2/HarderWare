using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
    public async Task SetEnabled_NotFound_WhenRowMissing()
    {
        await using var ctx = new WeatherDataContext(_db);
        var result = await LanguageToggle.SetEnabledAsync(ctx, languageId: 999, enabled: false);
        Assert.Equal(LanguageToggleOutcome.NotFound, result.Outcome);
    }
}