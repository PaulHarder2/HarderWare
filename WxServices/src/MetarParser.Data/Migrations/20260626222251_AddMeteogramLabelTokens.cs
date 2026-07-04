using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMeteogramLabelTokens : Migration
    {
        // WX-224 (Phase B): localize the in-image meteogram labels. Three new vocabulary
        // tokens — meteogram_wind / meteogram_rh / meteogram_temp; MeteogramWorker resolves
        // them per language and passes them to meteogram.py (the day-of-week ticks come from
        // CultureInfo, not a token). "T" is a token but seeded "T" for en — tokenized for
        // non-Latin future-proofing, not because it varies today.
        //
        // Also relabels the WX-223 MeteogramCaption so the legend beneath the chart now
        // *defines* the on-chart abbreviations — "temperature (T), relative humidity (RH)…".
        // WX-251 made the seed en-only — only the en (37) rows and the en caption relabel are
        // authored here; every target language (es/de/da/eo and any future one) generates
        // these via top-up (WX-250). Same InsertData row shape as the WX-171 seed (literal
        // columns), so SeedTemplateStore parses the en rows for the TokSeedParityTests gate.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WX-251: en-only seed. WX-171 already fail-closed guards 37=en (and runs first),
            // so no per-migration id guard is needed now that only the en (37) rows are authored.
            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            // ContextInfo is inlined as a literal on every row (not a const) so SeedTemplateStore's
            // row regex — which expects a quoted string in each column — parses the en rows for
            // the TokSeedParityTests gate.
            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "meteogram_wind", "Wind",   "In-image meteogram label for the wind panel. Keep short.", "Hint", true },
                { 37L, "meteogram_rh",   "RH",     "In-image meteogram label for the relative-humidity axis; (%) is appended by the renderer. Keep extremely short (about two characters).", "Hint", true },
                { 37L, "meteogram_temp", "T",      "In-image meteogram label for the temperature axis; the unit (°F/°C) is appended by the renderer. Keep extremely short — a single letter is ideal.", "Hint", true },
            });

            // Relabel the caption to define the abbreviations (gloss in parentheses, each
            // language using its own meteogram_rh/_temp value so the chart and caption agree).
            Relabel(migrationBuilder, "en", "MeteogramCaption", "Forecast of temperature (T), relative humidity (RH), and wind over time. Wind symbols point in the direction the wind is blowing, with more feathers indicating stronger winds.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the WX-223 caption (no abbreviation glosses).
            Relabel(migrationBuilder, "en", "MeteogramCaption", "Forecast of temperature, humidity, and wind over time. Wind symbols point in the direction the wind is blowing, with more feathers indicating stronger winds.");

            foreach (var langId in new long[] { 37L })
                foreach (var token in new[] { "meteogram_wind", "meteogram_rh", "meteogram_temp" })
                    migrationBuilder.DeleteData(
                        table: "LanguageTemplates",
                        keyColumns: new[] { "LanguageId", "Token" },
                        keyValues: new object[] { langId, token });
        }

        // One language's phrase update, keyed on (IsoCode, Token). Phrase values are
        // migration-internal constants (no user input) and apostrophe-free, so the
        // interpolation is neither an injection surface nor needs escaping. SeedTemplateStore
        // parses this 4-arg form (token named explicitly), unlike the WX-184 3-arg helper.
        private static void Relabel(MigrationBuilder migrationBuilder, string isoCode, string token, string phrase) =>
            migrationBuilder.Sql(
                "UPDATE lt SET lt.[Phrase] = N'" + phrase + "' " +
                "FROM [LanguageTemplates] lt INNER JOIN [Languages] l ON l.[Id] = lt.[LanguageId] " +
                "WHERE l.[IsoCode] = N'" + isoCode + "' AND lt.[Token] = N'" + token + "';");
    }
}