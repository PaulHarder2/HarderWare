using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRecipientStateSnapshotFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSnapshotFingerprint",
                table: "RecipientStates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSnapshotFingerprint",
                table: "RecipientStates",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }
    }
}
