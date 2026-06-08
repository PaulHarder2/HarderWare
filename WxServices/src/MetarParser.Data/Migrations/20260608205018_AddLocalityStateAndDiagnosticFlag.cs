using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalityStateAndDiagnosticFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SchemaVersion",
                table: "ForecastSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 4,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 3);

            migrationBuilder.AlterColumn<int>(
                name: "SchemaVersion",
                table: "CommittedSends",
                type: "int",
                nullable: false,
                defaultValue: 3,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 2);

            migrationBuilder.AddColumn<bool>(
                name: "IsDiagnostic",
                table: "CommittedSends",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "LocalityStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LocalityId = table.Column<long>(type: "bigint", nullable: false),
                    LastClaudeInputHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastSentInputHash = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastMetarIcao = table.Column<string>(type: "nchar(4)", fixedLength: true, maxLength: 4, nullable: true),
                    LastScheduledSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUnscheduledSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalityStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LocalityStates_Localities_LocalityId",
                        column: x => x.LocalityId,
                        principalTable: "Localities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_LocalityStates_LocalityId",
                table: "LocalityStates",
                column: "LocalityId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalityStates");

            migrationBuilder.DropColumn(
                name: "IsDiagnostic",
                table: "CommittedSends");

            migrationBuilder.AlterColumn<int>(
                name: "SchemaVersion",
                table: "ForecastSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 3,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 4);

            migrationBuilder.AlterColumn<int>(
                name: "SchemaVersion",
                table: "CommittedSends",
                type: "int",
                nullable: false,
                defaultValue: 2,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 3);
        }
    }
}
