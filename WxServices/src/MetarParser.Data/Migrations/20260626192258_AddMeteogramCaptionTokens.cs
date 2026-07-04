using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMeteogramCaptionTokens : Migration
    {
        // WX-223: localize the meteogram caption + alt text. Two new vocabulary tokens.
        // WX-251 made the seed en-only — only the en (37) rows are authored here; every
        // target language (es/de/da/eo and any future one) generates these via top-up
        // (WX-250), not a hardcoded seed. Same InsertData row shape as the WX-171 seed
        // (literal columns), so SeedTemplateStore's parser picks up the en rows for the
        // TokSeedParityTests gate.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WX-251: en-only seed. The WX-171 migration already fail-closed guards 37=en
            // (and runs first), so no per-migration id guard is needed now that only the
            // en (37) row is authored here.
            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "MeteogramCaption", "Forecast of temperature, humidity, and wind over time. Wind symbols point in the direction the wind is blowing, with more feathers indicating stronger winds.", "Caption beneath the forecast meteogram chart explaining what it shows.", "Hint", true },
                { 37L, "MeteogramAlt", "48-hour forecast meteogram", "Image alt text for the forecast meteogram chart (accessibility / when the image fails to load).", "Hint", true },
            });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var langId in new long[] { 37L })
                foreach (var token in new[] { "MeteogramCaption", "MeteogramAlt" })
                    migrationBuilder.DeleteData(
                        table: "LanguageTemplates",
                        keyColumns: new[] { "LanguageId", "Token" },
                        keyValues: new object[] { langId, token });
        }
    }
}