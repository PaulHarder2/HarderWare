using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptGlossaryTokens : Migration
    {
        // WX-238: anchor the free-composed narrative (WX-128) on approved vocabulary. Two parts:
        //
        // 1. SEPARATED phenomenon/probability vocabulary. The deterministic renderer uses fused
        //    composites (rain_likely, sev_storms_possible) because it can't glue phrases safely
        //    across languages. The narrative — where Claude handles grammar — should instead
        //    compose from the two SEPARATE axes: phenomenon ("severe storms") + probability
        //    ("possible"). Those standalone words don't exist yet, so 6 new tokens are seeded for
        //    en only (WX-251 en-only seed); every target language generates them via top-up
        //    (WX-250). en carries the reference phrasing and satisfies the Tok<->seed parity gate.
        //    Probability ladder tops out at "expected" — as certain as a forecast ever states
        //    (there is no "certain" tier).
        //
        // 2. The PromptGlossaryTokens registry (language-neutral) listing which concepts the
        //    reconciler injects into its prompt glossary: the 14 existing phenomenon nouns + the 6
        //    new tokens. The fused composites are deliberately excluded — they are band clauses, not
        //    the vocabulary the narrative composes with.
        //
        // Same literal-column InsertData shape as the WX-224 seed so SeedTemplateStore parses the
        // en rows for the TokSeedParityTests gate.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WX-251: en-only seed. WX-171 already fail-closed guards 37=en (and runs first),
            // so no per-migration id guard is needed now that only the en (37) rows are authored.

            // ── The language-neutral glossary registry ───────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "PromptGlossaryTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Token = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptGlossaryTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_PromptGlossaryTokens_Token",
                table: "PromptGlossaryTokens",
                column: "Token",
                unique: true);

            // ── Part 1: the 6 separated phenomenon/probability tokens (en only, WX-251) ───────────
            // ContextInfo is inlined as a literal on every row (not a const) so SeedTemplateStore's
            // row regex — which expects a quoted string in each column — parses the en rows for
            // the TokSeedParityTests gate.
            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "possible", "possible",       "Standalone forecast-probability word (lowest tier) for the free-composed narrative (WX-238); compose it with the SEPARATE phenomenon noun, do not fuse the two.", "Hint", true },
                { 37L, "likely", "likely",           "Standalone forecast-probability word (middle tier) for the free-composed narrative (WX-238); compose it with the separate phenomenon noun.", "Hint", true },
                { 37L, "expected", "expected",       "Standalone forecast-probability word (top tier — as certain as a forecast ever states) for the free-composed narrative (WX-238).", "Hint", true },
                { 37L, "storms", "storms",           "Standalone phenomenon noun (thunderstorms) for the free-composed narrative (WX-238); compose it with the separate probability word.", "Hint", true },
                { 37L, "severe_storms", "severe storms",       "Standalone phenomenon noun (severe thunderstorms) for the free-composed narrative (WX-238).", "Hint", true },
                { 37L, "severe_weather", "severe weather",     "Standalone phenomenon noun (hazardous severe weather) for the free-composed narrative (WX-238).", "Hint", true },
            });

            // ── Part 2: seed the glossary registry — the 14 existing phenomenon nouns + the 6 new
            // separated tokens (20). The fused likelihood composites (rain_likely, "Severe storms
            // possible", …) stay in LanguageTemplates for the renderer but are NOT anchored here —
            // the narrative composes likelihood/severity itself from the separated axes. Curatable
            // data: add/remove a row to change what the prompt glossary anchors, no code change.
            migrationBuilder.InsertData(
                table: "PromptGlossaryTokens",
                column: "Token",
                values: new object[]
                {
                    // phenomenon nouns (existing vocabulary)
                    "clear_and_dry",
                    "drizzle", "drizzle_freezing", "drizzle_light",
                    "rain", "rain_freezing", "rain_heavy", "rain_light", "rain_showers",
                    "snow", "snow_heavy", "snow_light", "snow_showers",
                    "wintry_mix",
                    // phenomenon nouns (new, separated from the composites)
                    "storms", "severe_storms", "severe_weather",
                    // probability ladder (new, separated from the composites)
                    "possible", "likely", "expected",
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromptGlossaryTokens");

            // Remove the WX-238 separated vocabulary from every language.
            migrationBuilder.Sql(
                "DELETE FROM LanguageTemplates WHERE Token IN "
                + "('possible', 'likely', 'expected', 'storms', 'severe_storms', 'severe_weather');");
        }
    }
}