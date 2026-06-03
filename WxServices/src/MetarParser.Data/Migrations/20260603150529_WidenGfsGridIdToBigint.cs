using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class WidenGfsGridIdToBigint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server cannot ALTER COLUMN a column while it backs the primary key /
            // clustered index, so EF's bare AlterColumn fails ("one or more objects
            // access this column"). Drop the clustered PK, widen Id int -> bigint (the
            // IDENTITY property is retained across the type change — it is not, and
            // cannot be, re-specified here), then recreate the PK. The two nonclustered
            // indexes carry the clustered key as their row locator, so they rebuild
            // automatically when the clustered PK is recreated.
            migrationBuilder.Sql("ALTER TABLE [GfsGrid] DROP CONSTRAINT [PK_GfsGrid];");
            migrationBuilder.Sql("ALTER TABLE [GfsGrid] ALTER COLUMN [Id] bigint NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE [GfsGrid] ADD CONSTRAINT [PK_GfsGrid] PRIMARY KEY CLUSTERED ([Id]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: bigint -> int. Safe only while every Id still fits in int range
            // (true immediately after Up and in a dev rollback); narrowing would fail if
            // the identity has since climbed past int.MaxValue — which is the whole reason
            // this migration exists, so this Down is a development convenience, not a
            // production rollback path.
            migrationBuilder.Sql("ALTER TABLE [GfsGrid] DROP CONSTRAINT [PK_GfsGrid];");
            migrationBuilder.Sql("ALTER TABLE [GfsGrid] ALTER COLUMN [Id] int NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE [GfsGrid] ADD CONSTRAINT [PK_GfsGrid] PRIMARY KEY CLUSTERED ([Id]);");
        }
    }
}
