using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipientPrecipUnitAndNumberFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NumberFormat",
                table: "Recipients",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            // Hand-edited from EF's scaffolded defaultValue: "" — backfill any
            // pre-existing rows with the entity's default ("in"), not empty string,
            // so existing US recipients keep inches (WX-133 Timezone precedent).
            migrationBuilder.AddColumn<string>(
                name: "PrecipUnit",
                table: "Recipients",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "in");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NumberFormat",
                table: "Recipients");

            migrationBuilder.DropColumn(
                name: "PrecipUnit",
                table: "Recipients");
        }
    }
}
