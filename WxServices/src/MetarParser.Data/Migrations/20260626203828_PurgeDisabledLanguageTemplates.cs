using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class PurgeDisabledLanguageTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WX-222: enforce "a language's templates exist iff it is enabled + generated"
            // retroactively. Purge LanguageTemplates for every currently-DISABLED language (the
            // fr/pt orphans today) and reset their generation state, so a future re-enable
            // regenerates a complete, current set — and so the disabled orphans stop loading and
            // tripping the WX-171 startup completeness check. Going forward, the WxManager disable
            // handler purges on disable; this migration brings the existing data to that invariant.
            migrationBuilder.Sql(
                "DELETE lt FROM [LanguageTemplates] lt " +
                "INNER JOIN [Languages] l ON l.[Id] = lt.[LanguageId] " +
                "WHERE l.[IsEnabled] = 0;");
            migrationBuilder.Sql(
                "UPDATE [Languages] SET [GeneratedAtUtc] = NULL, [GenerationError] = NULL WHERE [IsEnabled] = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-way data cleanup: the purged rows were regenerable artifacts, not source data,
            // so there is nothing to restore. Re-enabling a language regenerates them.
        }
    }
}