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
        //    ("possible"). Those standalone words don't exist yet, so 6 new tokens are seeded in
        //    all five enabled languages. en/es carry the reference/high-confidence phrasing (and
        //    satisfy the Tok<->seed parity gate); de/da/eo are extracted from the existing reviewed
        //    composites (the runtime-generated vocabulary), advisory until the next Translation-QA
        //    run reviews them (Paul, WX-238). Probability ladder tops out at "expected" — as certain
        //    as a forecast ever states (there is no "certain" tier).
        //
        // 2. The PromptGlossaryTokens registry (language-neutral) listing which concepts the
        //    reconciler injects into its prompt glossary: the 14 existing phenomenon nouns + the 6
        //    new tokens. The fused composites are deliberately excluded — they are band clauses, not
        //    the vocabulary the narrative composes with.
        //
        // Same literal-column InsertData shape as the WX-224 seed so SeedTemplateStore parses the
        // en/es rows for the TokSeedParityTests gate. The same English hint repeats per language
        // (ContextKind = Hint), by design.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fail closed if the WX-166 ISO-seed ids ever drift — abort before seeding the wrong language.
            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 37 AND IsoCode = 'en') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 39 AND IsoCode = 'es') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 32 AND IsoCode = 'de') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 31 AND IsoCode = 'da') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 38 AND IsoCode = 'eo') " +
                "THROW 50000, 'WX-238 seed: Languages Id 37/39/32/31/38 are not en/es/de/da/eo as expected (WX-166 ISO-seed drift). Aborting before seeding the separated narrative vocabulary.', 1;");

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

            // ── Part 1: the 6 separated phenomenon/probability tokens, in all 5 languages ─────────
            // ContextInfo is inlined as a literal on every row (not a const) so SeedTemplateStore's
            // row regex — which expects a quoted string in each column — parses the en/es rows for
            // the TokSeedParityTests gate. The same English hint repeats per language (Hint kind).
            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "possible", "possible",       "Standalone forecast-probability word (lowest tier) for the free-composed narrative (WX-238); compose it with the SEPARATE phenomenon noun, do not fuse the two.", "Hint", true },
                { 39L, "possible", "posible",        "Standalone forecast-probability word (lowest tier) for the free-composed narrative (WX-238); compose it with the SEPARATE phenomenon noun, do not fuse the two.", "Hint", true },
                { 32L, "possible", "möglich",        "Standalone forecast-probability word (lowest tier) for the free-composed narrative (WX-238); compose it with the SEPARATE phenomenon noun, do not fuse the two.", "Hint", true },
                { 31L, "possible", "mulig",          "Standalone forecast-probability word (lowest tier) for the free-composed narrative (WX-238); compose it with the SEPARATE phenomenon noun, do not fuse the two.", "Hint", true },
                { 38L, "possible", "ebla",           "Standalone forecast-probability word (lowest tier) for the free-composed narrative (WX-238); compose it with the SEPARATE phenomenon noun, do not fuse the two.", "Hint", true },

                { 37L, "likely", "likely",           "Standalone forecast-probability word (middle tier) for the free-composed narrative (WX-238); compose it with the separate phenomenon noun.", "Hint", true },
                { 39L, "likely", "probable",         "Standalone forecast-probability word (middle tier) for the free-composed narrative (WX-238); compose it with the separate phenomenon noun.", "Hint", true },
                { 32L, "likely", "wahrscheinlich",   "Standalone forecast-probability word (middle tier) for the free-composed narrative (WX-238); compose it with the separate phenomenon noun.", "Hint", true },
                { 31L, "likely", "sandsynlig",       "Standalone forecast-probability word (middle tier) for the free-composed narrative (WX-238); compose it with the separate phenomenon noun.", "Hint", true },
                { 38L, "likely", "verŝajna",         "Standalone forecast-probability word (middle tier) for the free-composed narrative (WX-238); compose it with the separate phenomenon noun.", "Hint", true },

                { 37L, "expected", "expected",       "Standalone forecast-probability word (top tier — as certain as a forecast ever states) for the free-composed narrative (WX-238).", "Hint", true },
                { 39L, "expected", "previsto",       "Standalone forecast-probability word (top tier — as certain as a forecast ever states) for the free-composed narrative (WX-238).", "Hint", true },
                { 32L, "expected", "erwartet",       "Standalone forecast-probability word (top tier — as certain as a forecast ever states) for the free-composed narrative (WX-238).", "Hint", true },
                { 31L, "expected", "forventet",      "Standalone forecast-probability word (top tier — as certain as a forecast ever states) for the free-composed narrative (WX-238).", "Hint", true },
                { 38L, "expected", "atendata",       "Standalone forecast-probability word (top tier — as certain as a forecast ever states) for the free-composed narrative (WX-238).", "Hint", true },

                { 37L, "storms", "storms",           "Standalone phenomenon noun (thunderstorms) for the free-composed narrative (WX-238); compose it with the separate probability word.", "Hint", true },
                { 39L, "storms", "tormentas",        "Standalone phenomenon noun (thunderstorms) for the free-composed narrative (WX-238); compose it with the separate probability word.", "Hint", true },
                { 32L, "storms", "Gewitter",         "Standalone phenomenon noun (thunderstorms) for the free-composed narrative (WX-238); compose it with the separate probability word.", "Hint", true },
                { 31L, "storms", "storme",           "Standalone phenomenon noun (thunderstorms) for the free-composed narrative (WX-238); compose it with the separate probability word.", "Hint", true },
                { 38L, "storms", "stormoj",          "Standalone phenomenon noun (thunderstorms) for the free-composed narrative (WX-238); compose it with the separate probability word.", "Hint", true },

                { 37L, "severe_storms", "severe storms",       "Standalone phenomenon noun (severe thunderstorms) for the free-composed narrative (WX-238).", "Hint", true },
                { 39L, "severe_storms", "tormentas severas",   "Standalone phenomenon noun (severe thunderstorms) for the free-composed narrative (WX-238).", "Hint", true },
                { 32L, "severe_storms", "schwere Gewitter",    "Standalone phenomenon noun (severe thunderstorms) for the free-composed narrative (WX-238).", "Hint", true },
                { 31L, "severe_storms", "kraftige storme",     "Standalone phenomenon noun (severe thunderstorms) for the free-composed narrative (WX-238).", "Hint", true },
                { 38L, "severe_storms", "severaj stormoj",     "Standalone phenomenon noun (severe thunderstorms) for the free-composed narrative (WX-238).", "Hint", true },

                { 37L, "severe_weather", "severe weather",     "Standalone phenomenon noun (hazardous severe weather) for the free-composed narrative (WX-238).", "Hint", true },
                { 39L, "severe_weather", "tiempo severo",      "Standalone phenomenon noun (hazardous severe weather) for the free-composed narrative (WX-238).", "Hint", true },
                { 32L, "severe_weather", "Unwetter",           "Standalone phenomenon noun (hazardous severe weather) for the free-composed narrative (WX-238).", "Hint", true },
                { 31L, "severe_weather", "kraftigt vejr",      "Standalone phenomenon noun (hazardous severe weather) for the free-composed narrative (WX-238).", "Hint", true },
                { 38L, "severe_weather", "severa vetero",      "Standalone phenomenon noun (hazardous severe weather) for the free-composed narrative (WX-238).", "Hint", true },
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