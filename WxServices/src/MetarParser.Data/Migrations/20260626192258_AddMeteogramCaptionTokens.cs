using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMeteogramCaptionTokens : Migration
    {
        // WX-223: localize the meteogram caption + alt text. Two new vocabulary tokens
        // seeded for the five enabled languages (en authoritative; es/de/da/eo are
        // Claude-generated, flagged for the WX-214 independent back-check; the disabled
        // fr/pt orphans are skipped per WX-222). Same InsertData row shape as the WX-171
        // seed (literal columns), so SeedTemplateStore's parser picks up the en/es rows
        // for the TokSeedParityTests gate.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail closed if the WX-166 ISO-seed ids ever drift — the WX-171 seed guards 37/39 (en/es);
            // this migration also writes 32/31/38 (de/da/eo) by id, so it guards all five before seeding.
            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 37 AND IsoCode = 'en') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 39 AND IsoCode = 'es') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 32 AND IsoCode = 'de') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 31 AND IsoCode = 'da') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 38 AND IsoCode = 'eo') " +
                "THROW 50000, 'WX-223 seed: Languages Id 37/39/32/31/38 are not en/es/de/da/eo as expected (WX-166 ISO-seed drift). Aborting before seeding meteogram tokens.', 1;");

            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "MeteogramCaption", "Forecast of temperature, humidity, and wind over time. Wind symbols point in the direction the wind is blowing, with more feathers indicating stronger winds.", "Caption beneath the forecast meteogram chart explaining what it shows.", "Hint", true },
                { 37L, "MeteogramAlt", "48-hour forecast meteogram", "Image alt text for the forecast meteogram chart (accessibility / when the image fails to load).", "Hint", true },

                { 39L, "MeteogramCaption", "Pronóstico de temperatura, humedad y viento a lo largo del tiempo. Los símbolos de viento apuntan hacia la dirección en la que sopla el viento, y más plumas indican vientos más fuertes.", "Caption beneath the forecast meteogram chart explaining what it shows.", "Hint", true },
                { 39L, "MeteogramAlt", "Meteograma de pronóstico de 48 horas", "Image alt text for the forecast meteogram chart (accessibility / when the image fails to load).", "Hint", true },

                { 32L, "MeteogramCaption", "Vorhersage von Temperatur, Luftfeuchtigkeit und Wind im Zeitverlauf. Die Windsymbole zeigen in die Richtung, in die der Wind weht; mehr Fähnchen bedeuten stärkeren Wind.", "Caption beneath the forecast meteogram chart explaining what it shows.", "Hint", true },
                { 32L, "MeteogramAlt", "48-Stunden-Vorhersage-Meteogramm", "Image alt text for the forecast meteogram chart (accessibility / when the image fails to load).", "Hint", true },

                { 31L, "MeteogramCaption", "Prognose for temperatur, luftfugtighed og vind over tid. Vindsymbolerne peger i den retning, vinden blæser, og flere fjer angiver kraftigere vind.", "Caption beneath the forecast meteogram chart explaining what it shows.", "Hint", true },
                { 31L, "MeteogramAlt", "48-timers prognose-meteogram", "Image alt text for the forecast meteogram chart (accessibility / when the image fails to load).", "Hint", true },

                { 38L, "MeteogramCaption", "Prognozo de temperaturo, humideco kaj vento laŭ la tempo. La ventosimboloj montras la direkton al kiu blovas la vento, kaj pli da plumoj indikas pli fortan venton.", "Caption beneath the forecast meteogram chart explaining what it shows.", "Hint", true },
                { 38L, "MeteogramAlt", "48-hora prognoza meteogramo", "Image alt text for the forecast meteogram chart (accessibility / when the image fails to load).", "Hint", true },
            });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var langId in new long[] { 37L, 39L, 32L, 31L, 38L })
                foreach (var token in new[] { "MeteogramCaption", "MeteogramAlt" })
                    migrationBuilder.DeleteData(
                        table: "LanguageTemplates",
                        keyColumns: new[] { "LanguageId", "Token" },
                        keyValues: new object[] { langId, token });
        }
    }
}