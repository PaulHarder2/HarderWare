using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillSeededLanguageReady : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WX-172: the generation-on-enable cycle treats an enabled language with
            // GeneratedAtUtc IS NULL as PENDING and (re)generates it. The WX-171 seed
            // left en (the authored baseline) enabled and fully templated but UNstamped,
            // so without this it would read PENDING and be needlessly re-translated.
            // Stamp every enabled, not-yet-stamped language whose representable templates
            // already cover the full baseline (en) token set — i.e. it is already complete
            // — as READY. This is the SQL form of SupportedLanguages.HasCompleteTemplates.
            //
            // WX-251 note: the seed is now en-only, so on a fresh DB this set-based test
            // stamps en only — es (still enabled, but no longer seeded) is incomplete here
            // and correctly stays PENDING until top-up (WX-250) generates it. No literal
            // es handling is needed; "stop stamping es" falls out of removing the es seed.
            // A complete set is the readiness signal; the stamp is the apply time.
            migrationBuilder.Sql("""
                UPDATE Languages
                SET GeneratedAtUtc = SYSUTCDATETIME()
                WHERE IsEnabled = 1
                  AND GeneratedAtUtc IS NULL
                  AND GenerationError IS NULL
                  AND NULLIF(LTRIM(RTRIM(CultureName)), '') IS NOT NULL
                  AND EXISTS (SELECT 1 FROM LanguageTemplates t WHERE t.LanguageId = Languages.Id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM LanguageTemplates b
                      JOIN Languages bl ON bl.Id = b.LanguageId AND bl.IsoCode = 'en'
                      WHERE b.Representable = 1
                        AND NOT EXISTS (
                            SELECT 1 FROM LanguageTemplates t
                            WHERE t.LanguageId = Languages.Id AND t.Token = b.Token AND t.Representable = 1
                        )
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally a no-op. GeneratedAtUtc is a benign readiness marker; clearing
            // it on rollback could not distinguish a backfilled seed from a legitimately
            // generated language, and would wrongly re-queue the latter for regeneration.
        }
    }
}
