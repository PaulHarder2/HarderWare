using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddValidatorUseToLanguageTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ValidatorUse",
                table: "LanguageTemplates",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "No");   // WX-335: existing rows are render-only (No); DayPart 'Yes' flags loaded per enabled language by the run-once script (non-en DayPart rows are runtime-generated, unreachable by a migration)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ValidatorUse",
                table: "LanguageTemplates");
        }
    }
}
