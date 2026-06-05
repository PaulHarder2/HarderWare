using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipientLocalityFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LocalityId",
                table: "Recipients",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipients_LocalityId",
                table: "Recipients",
                column: "LocalityId");

            migrationBuilder.AddForeignKey(
                name: "FK_Recipients_Localities_LocalityId",
                table: "Recipients",
                column: "LocalityId",
                principalTable: "Localities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Recipients_Localities_LocalityId",
                table: "Recipients");

            migrationBuilder.DropIndex(
                name: "IX_Recipients_LocalityId",
                table: "Recipients");

            migrationBuilder.DropColumn(
                name: "LocalityId",
                table: "Recipients");
        }
    }
}
