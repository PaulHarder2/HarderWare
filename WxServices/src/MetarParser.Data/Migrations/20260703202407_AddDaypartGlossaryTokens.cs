using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDaypartGlossaryTokens : Migration
    {
        // WX-244: anchor the three named daypart words (morning / afternoon / evening) in the
        // narrative glossary so the free-composed prose uses the approved daypart term for each
        // language — in particular es evening = "Tarde-Noche", not the ambiguous "Noche". The
        // per-language phrases already live in LanguageTemplates (seeded / curated); this only adds
        // the concepts to the language-neutral PromptGlossaryTokens registry (WX-238). Additive; no
        // schema change; auto-applies at startup. (00-06 stays name-less by WX-190 design; the
        // narrative handles it via a prompt rule, not a token.)

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "PromptGlossaryTokens",
                column: "Token",
                values: new object[] { "PartAfternoon", "PartEvening", "PartMorning" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PromptGlossaryTokens",
                keyColumn: "Token",
                keyValues: new object[] { "PartAfternoon", "PartEvening", "PartMorning" });
        }
    }
}
