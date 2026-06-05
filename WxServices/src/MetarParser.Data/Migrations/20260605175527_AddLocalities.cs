using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Localities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MetarIcao = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TafIcao = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    CentroidLat = table.Column<double>(type: "float", nullable: true),
                    CentroidLon = table.Column<double>(type: "float", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Localities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Localities_Name",
                table: "Localities",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Localities");
        }
    }
}
