using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCommittedSends : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommittedSends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ForecastSnapshotId = table.Column<int>(type: "int", nullable: false),
                    RecipientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReasoningTrace = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmailBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommittedSends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommittedSends_ForecastSnapshots_ForecastSnapshotId",
                        column: x => x.ForecastSnapshotId,
                        principalTable: "ForecastSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommittedSends_ForecastSnapshotId",
                table: "CommittedSends",
                column: "ForecastSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_CommittedSends_Recipient_CreatedAt",
                table: "CommittedSends",
                columns: new[] { "RecipientId", "CreatedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommittedSends");
        }
    }
}
