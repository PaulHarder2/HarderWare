using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRejectedGateMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastRejectedGateCriteria",
                table: "LocalityStates",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRejectedGateUtc",
                table: "LocalityStates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRejectedInputHash",
                table: "LocalityStates",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRejectedGateCriteria",
                table: "LocalityStates");

            migrationBuilder.DropColumn(
                name: "LastRejectedGateUtc",
                table: "LocalityStates");

            migrationBuilder.DropColumn(
                name: "LastRejectedInputHash",
                table: "LocalityStates");
        }
    }
}
