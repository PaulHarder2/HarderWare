using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForecastSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForecastSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StationIcao = table.Column<string>(type: "nchar(4)", fixedLength: true, maxLength: 4, nullable: false),
                    GeneratedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_ForecastSnapshots_Station_GeneratedAt",
                table: "ForecastSnapshots",
                columns: new[] { "StationIcao", "GeneratedAtUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForecastSnapshots");
        }
    }
}
