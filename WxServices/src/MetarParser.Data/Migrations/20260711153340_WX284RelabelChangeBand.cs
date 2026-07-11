using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class WX284RelabelChangeBand : Migration
    {
        // WX-284 (step 3): reframe the change-band label from a change-LIST header
        // ("What's changed:") to one that fronts the single reason this report matters
        // ("Why this update:"). The band now names the one triggering change, not an
        // enumeration (see the changeSummary contract in ReconcilerPrompts). Data-only:
        // the renderer already reads Tok.WhatsChangedLabel, so no code moves with it.
        // Keyed on (IsoCode, Token) so it is robust to the row's surrogate id. WX-251
        // made the seed en-only, so this relabels the `en` row only; the en row is the
        // generation baseline (ReportWorker), so every target language re-derives the new
        // label via top-up (WX-250) — existing curated target-language rows are re-curated
        // by QA. No-op on a DB already carrying the new label.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            Relabel(migrationBuilder, "en", "WhatsChangedLabel", "Why this update:");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            Relabel(migrationBuilder, "en", "WhatsChangedLabel", "What's changed:");

        // One language's phrase update, keyed on (IsoCode, Token). The values are
        // migration-internal constants (no user input), so no injection surface; the
        // apostrophe in the revert phrase is doubled for the SQL string literal.
        private static void Relabel(MigrationBuilder migrationBuilder, string isoCode, string token, string phrase) =>
            migrationBuilder.Sql(
                "UPDATE lt SET lt.[Phrase] = N'" + phrase.Replace("'", "''") + "' " +
                "FROM [LanguageTemplates] lt INNER JOIN [Languages] l ON l.[Id] = lt.[LanguageId] " +
                "WHERE l.[IsoCode] = N'" + isoCode + "' AND lt.[Token] = N'" + token + "';");
    }
}
