using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class RelabelCurrentToReportedConditions : Migration
    {
        // WX-184: relabel the "Current Conditions" heading (and its per-language
        // equivalents) to "Reported Conditions". An emailed report is never live —
        // by the time a reader sees it the observation is a snapshot — so the honest
        // heading names what was reported, not what is "current". Data-only change:
        // the renderer already reads this label from the Tok.CurrentConditionsHeading
        // template, so no code moves with it.
        //
        // Keyed on IsoCode (the stable language identity) + Token, so it is robust to
        // each row's surrogate id. WX-251 made the seed en-only, so this relabels the
        // `en` row only; the en row doubles as the generation baseline (ReportWorker),
        // so every target language picks up "Reported Conditions" when top-up (WX-250)
        // generates it. No-op on the already-migrated prod DB (en already relabeled).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Relabel(migrationBuilder, "en", "Reported Conditions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Relabel(migrationBuilder, "en", "Current Conditions");
        }

        // One language's heading update, keyed on (IsoCode, Token). The phrase values
        // are migration-internal constants (no user input), so the interpolation is
        // not an injection surface; the apostrophe-free phrases need no escaping.
        private static void Relabel(MigrationBuilder migrationBuilder, string isoCode, string phrase) =>
            migrationBuilder.Sql(
                "UPDATE lt SET lt.[Phrase] = N'" + phrase + "' " +
                "FROM [LanguageTemplates] lt INNER JOIN [Languages] l ON l.[Id] = lt.[LanguageId] " +
                "WHERE l.[IsoCode] = N'" + isoCode + "' AND lt.[Token] = N'CurrentConditionsHeading';");
    }
}