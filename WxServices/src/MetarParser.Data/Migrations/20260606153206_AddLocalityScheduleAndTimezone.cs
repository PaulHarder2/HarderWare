using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalityScheduleAndTimezone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduledSendHours",
                table: "Localities",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // Hand-edited from EF's scaffolded defaultValue: "" — backfill any
            // pre-existing rows with the entity's default ("UTC"), not empty string.
            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "Localities",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduledSendHours",
                table: "Localities");

            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "Localities");
        }
    }
}
