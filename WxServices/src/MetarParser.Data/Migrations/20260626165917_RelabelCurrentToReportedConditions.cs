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
        // each row's surrogate id. Scope is the five ENABLED languages only; the `en`
        // row doubles as the generation baseline (ReportWorker), so future-enabled
        // languages translate "Reported Conditions" automatically. The disabled fr/pt
        // orphans are intentionally left untouched — their lifecycle is WX-222.
        //
        // The de/da/eo phrasings are Claude-generated and flagged for a native /
        // independent-model back-check (WX-214); en/es are high-confidence.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Relabel(migrationBuilder, "en", "Reported Conditions");
            Relabel(migrationBuilder, "es", "Condiciones reportadas");
            Relabel(migrationBuilder, "de", "Gemeldete Wetterlage");
            Relabel(migrationBuilder, "da", "Rapporterede forhold");
            Relabel(migrationBuilder, "eo", "Raportitaj Kondiĉoj");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            Relabel(migrationBuilder, "en", "Current Conditions");
            Relabel(migrationBuilder, "es", "Condiciones actuales");
            Relabel(migrationBuilder, "de", "Aktuelle Wetterlage");
            Relabel(migrationBuilder, "da", "Aktuelle forhold");
            Relabel(migrationBuilder, "eo", "Aktualaj Kondiĉoj");
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