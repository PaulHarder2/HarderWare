using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoonMidnightTokens : Migration
    {
        // WX-256: two curated SOFT time-word tokens — `noon` (12:00 midday) and `midnight` (00:00).
        // Seeded en-only (WX-247); every target language generates + curates them via WX-250 top-up.
        // Soft (Tok.Soft) means a language missing one still sends (the renderer degrades to the
        // culture 12-hour form), so this deploys with NO fail-closed suppression window. Same
        // literal-column InsertData shape as the WX-171 seed so SeedTemplateStore parses the en rows
        // for the TokSeedParityTests gate. WX-171 already fail-closed guards 37=en (runs first).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "noon", "noon", "The word for 12:00 noon (midday, the meridian), rendered in place of a 12:00 PM clock time; used bare in prose (e.g. by noon) and after a time in a schedule statement (e.g. 12:00 noon). Give the language's own noon word.", "Hint", true },
                { 37L, "midnight", "midnight", "The word for 12:00 midnight (00:00), used after a time in a schedule statement to state a precise clock time (e.g. 12:00 midnight). Give the language's own midnight word.", "Hint", true },
            });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var token in new[] { "noon", "midnight" })
                migrationBuilder.DeleteData(
                    table: "LanguageTemplates",
                    keyColumns: new[] { "LanguageId", "Token" },
                    keyValues: new object[] { 37L, token });
        }
    }
}
