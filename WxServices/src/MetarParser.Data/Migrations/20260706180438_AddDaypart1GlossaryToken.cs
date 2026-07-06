using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDaypart1GlossaryToken : Migration
    {
        // WX-264: anchor the 00:00-06:00 daypart word (DayPart1, "early hours") in the narrative
        // glossary, so the free-composed prose uses each language's approved pre-dawn term
        // (es "madrugada", de "frühe Morgenstunden", …) when it labels that block — the same way
        // WX-244 anchored the morning/afternoon/evening words (now DayPart2/3/4). DayPart1 got its
        // vocabulary in WX-265 but was deliberately left out of the glossary until it was consumed
        // in prose; WX-264 is that consumer. Additive; no schema change; auto-applies at startup.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "PromptGlossaryTokens",
                column: "Token",
                value: "DayPart1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PromptGlossaryTokens",
                keyColumn: "Token",
                keyValue: "DayPart1");
        }
    }
}
