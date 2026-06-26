using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMeteogramLabelTokens : Migration
    {
        // WX-224 (Phase B): localize the in-image meteogram labels. Three new vocabulary
        // tokens — meteogram_wind / meteogram_rh / meteogram_temp — seeded for the five
        // enabled languages; MeteogramWorker resolves them per language and passes them to
        // meteogram.py (the day-of-week ticks come from CultureInfo, not a token). "T" is a
        // token but seeded "T" for every Latin-5 language — tokenized for non-Latin future-
        // proofing, not because it varies today.
        //
        // Also relabels the WX-223 MeteogramCaption so the legend beneath the chart now
        // *defines* the on-chart abbreviations — "temperature (T), relative humidity (RH)…"
        // — keeping each language's caption gloss consistent with its meteogram_rh/_temp
        // token. en is authoritative + the generation baseline (a future-enabled language
        // translates these automatically); es is high-confidence; de/da/eo (the rF/RF/RH
        // abbreviations especially) are flagged for the WX-214 independent back-check. The
        // disabled fr/pt orphans are skipped (WX-222). Same InsertData row shape as the
        // WX-171 seed (literal columns), so SeedTemplateStore parses the en/es rows for the
        // TokSeedParityTests gate.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail closed if the WX-166 ISO-seed ids ever drift (en/es guarded by WX-171,
            // de/da/eo written here by id) — abort before seeding against the wrong language.
            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 37 AND IsoCode = 'en') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 39 AND IsoCode = 'es') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 32 AND IsoCode = 'de') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 31 AND IsoCode = 'da') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 38 AND IsoCode = 'eo') " +
                "THROW 50000, 'WX-224 seed: Languages Id 37/39/32/31/38 are not en/es/de/da/eo as expected (WX-166 ISO-seed drift). Aborting before seeding meteogram label tokens.', 1;");

            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            // ContextInfo is inlined as a literal on every row (not a const) so SeedTemplateStore's
            // row regex — which expects a quoted string in each column — parses the en/es rows for
            // the TokSeedParityTests gate. Same English hint repeats per language by design.
            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "meteogram_wind", "Wind",   "In-image meteogram label for the wind panel. Keep short.", "Hint", true },
                { 37L, "meteogram_rh",   "RH",     "In-image meteogram label for the relative-humidity axis; (%) is appended by the renderer. Keep extremely short (about two characters).", "Hint", true },
                { 37L, "meteogram_temp", "T",      "In-image meteogram label for the temperature axis; the unit (°F/°C) is appended by the renderer. Keep extremely short — a single letter is ideal.", "Hint", true },

                { 39L, "meteogram_wind", "Viento", "In-image meteogram label for the wind panel. Keep short.", "Hint", true },
                { 39L, "meteogram_rh",   "HR",     "In-image meteogram label for the relative-humidity axis; (%) is appended by the renderer. Keep extremely short (about two characters).", "Hint", true },
                { 39L, "meteogram_temp", "T",      "In-image meteogram label for the temperature axis; the unit (°F/°C) is appended by the renderer. Keep extremely short — a single letter is ideal.", "Hint", true },

                { 32L, "meteogram_wind", "Wind",   "In-image meteogram label for the wind panel. Keep short.", "Hint", true },
                { 32L, "meteogram_rh",   "RF",     "In-image meteogram label for the relative-humidity axis; (%) is appended by the renderer. Keep extremely short (about two characters).", "Hint", true },
                { 32L, "meteogram_temp", "T",      "In-image meteogram label for the temperature axis; the unit (°F/°C) is appended by the renderer. Keep extremely short — a single letter is ideal.", "Hint", true },

                { 31L, "meteogram_wind", "Vind",   "In-image meteogram label for the wind panel. Keep short.", "Hint", true },
                { 31L, "meteogram_rh",   "RF",     "In-image meteogram label for the relative-humidity axis; (%) is appended by the renderer. Keep extremely short (about two characters).", "Hint", true },
                { 31L, "meteogram_temp", "T",      "In-image meteogram label for the temperature axis; the unit (°F/°C) is appended by the renderer. Keep extremely short — a single letter is ideal.", "Hint", true },

                { 38L, "meteogram_wind", "Vento",  "In-image meteogram label for the wind panel. Keep short.", "Hint", true },
                { 38L, "meteogram_rh",   "RH",     "In-image meteogram label for the relative-humidity axis; (%) is appended by the renderer. Keep extremely short (about two characters).", "Hint", true },
                { 38L, "meteogram_temp", "T",      "In-image meteogram label for the temperature axis; the unit (°F/°C) is appended by the renderer. Keep extremely short — a single letter is ideal.", "Hint", true },
            });

            // Relabel the caption to define the abbreviations (gloss in parentheses, each
            // language using its own meteogram_rh/_temp value so the chart and caption agree).
            Relabel(migrationBuilder, "en", "MeteogramCaption", "Forecast of temperature (T), relative humidity (RH), and wind over time. Wind symbols point in the direction the wind is blowing, with more feathers indicating stronger winds.");
            Relabel(migrationBuilder, "es", "MeteogramCaption", "Pronóstico de temperatura (T), humedad relativa (HR) y viento a lo largo del tiempo. Los símbolos de viento apuntan hacia la dirección en la que sopla el viento, y más plumas indican vientos más fuertes.");
            Relabel(migrationBuilder, "de", "MeteogramCaption", "Vorhersage von Temperatur (T), relativer Luftfeuchtigkeit (RF) und Wind im Zeitverlauf. Die Windsymbole zeigen in die Richtung, in die der Wind weht; mehr Fähnchen bedeuten stärkeren Wind.");
            Relabel(migrationBuilder, "da", "MeteogramCaption", "Prognose for temperatur (T), relativ luftfugtighed (RF) og vind over tid. Vindsymbolerne peger i den retning, vinden blæser, og flere fjer angiver kraftigere vind.");
            Relabel(migrationBuilder, "eo", "MeteogramCaption", "Prognozo de temperaturo (T), relativa humideco (RH) kaj vento laŭ la tempo. La ventosimboloj montras la direkton al kiu blovas la vento, kaj pli da plumoj indikas pli fortan venton.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore the WX-223 captions (no abbreviation glosses).
            Relabel(migrationBuilder, "en", "MeteogramCaption", "Forecast of temperature, humidity, and wind over time. Wind symbols point in the direction the wind is blowing, with more feathers indicating stronger winds.");
            Relabel(migrationBuilder, "es", "MeteogramCaption", "Pronóstico de temperatura, humedad y viento a lo largo del tiempo. Los símbolos de viento apuntan hacia la dirección en la que sopla el viento, y más plumas indican vientos más fuertes.");
            Relabel(migrationBuilder, "de", "MeteogramCaption", "Vorhersage von Temperatur, Luftfeuchtigkeit und Wind im Zeitverlauf. Die Windsymbole zeigen in die Richtung, in die der Wind weht; mehr Fähnchen bedeuten stärkeren Wind.");
            Relabel(migrationBuilder, "da", "MeteogramCaption", "Prognose for temperatur, luftfugtighed og vind over tid. Vindsymbolerne peger i den retning, vinden blæser, og flere fjer angiver kraftigere vind.");
            Relabel(migrationBuilder, "eo", "MeteogramCaption", "Prognozo de temperaturo, humideco kaj vento laŭ la tempo. La ventosimboloj montras la direkton al kiu blovas la vento, kaj pli da plumoj indikas pli fortan venton.");

            foreach (var langId in new long[] { 37L, 39L, 32L, 31L, 38L })
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