using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipientStateLastSentInputHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSentInputHash",
                table: "RecipientStates",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // WX-108: seed the new column for recipients that predate it. Without
            // this, an existing recipient's LastSentInputHash is NULL, so the first
            // post-deploy cycle reads every input as "changed since last send" —
            // forcing freshGuidanceSinceLastSend true (disabling the severe-flag
            // hysteresis) and mislabelling the prompt's changed_since_last_sent_report.
            // LastClaudeInputHash is the best available proxy: for a recipient whose
            // last Claude call was an actual send it is exact, and otherwise it is a
            // recent identity that biases conservatively (toward suppression). The
            // first real send overwrites it with the precise value.
            migrationBuilder.Sql(
                "UPDATE RecipientStates SET LastSentInputHash = LastClaudeInputHash " +
                "WHERE LastSentInputHash IS NULL AND LastClaudeInputHash IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSentInputHash",
                table: "RecipientStates");
        }
    }
}
