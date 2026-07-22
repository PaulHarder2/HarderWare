using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSpanTokens : Migration
    {
        // WX-239: two curated SOFT span-preposition tokens for the free-composed narrative —
        // span_through (INCLUSIVE: "through Saturday" covers all of Saturday) and span_until (a
        // boundary read as "up to", left ambiguous by design). Seeded en-only (WX-247); de/es/eo
        // generate + curate their phrases via WX-250 top-up, so they are legitimately absent for a
        // window — hence SOFT (Tok.Soft): a language missing one degrades to the LLM's free rendering
        // rather than suppressing the report, so this deploys with NO fail-closed suppression window.
        // ContextKind = Hint: ContextInfo is an English usage gloss the generator reads to translate
        // correctly and never translates. Never rendered by the deterministic renderer — these exist
        // only to anchor the reconciler prompt glossary (Part 2 below). Same literal-column InsertData
        // shape as the WX-171 seed so SeedTemplateStore parses the en rows for the TokSeedParityTests
        // gate; ContextInfo uses single quotes only (the parser's per-field regex forbids a double-quote).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Part 1: the two en span tokens (en only, WX-247) ──────────────────────────────────
            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                { 37L, "span_through", "through", "Preposition marking an INCLUSIVE end to a time span: 'through Saturday' covers all of Saturday — the named day is part of the span. Render with the target language's inclusive-span form so the last day is not dropped; German requires 'bis einschließlich [day]', not a bare 'bis [day]' (which reads as up-to and truncates the span by a day).", "Hint", true },
                { 37L, "span_until", "until", "Preposition marking a time boundary read as 'up to' a point: 'until Saturday'. English 'until' is genuinely ambiguous about whether the endpoint itself is included, so do NOT force a strictly-exclusive rendering — use the target language's ordinary 'until' word, carrying the same looseness the English has; German 'bis [day]' is correct here.", "Hint", true },
            });

            // ── Part 2: anchor both concepts in the language-neutral prompt glossary (WX-238) so
            // NarrativeGlossary.Build injects them into the reconciler's per-report system block.
            migrationBuilder.InsertData(
                table: "PromptGlossaryTokens",
                column: "Token",
                values: new object[] { "span_through", "span_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PromptGlossaryTokens",
                keyColumn: "Token",
                keyValues: new object[] { "span_through", "span_until" });

            foreach (var token in new[] { "span_through", "span_until" })
                migrationBuilder.DeleteData(
                    table: "LanguageTemplates",
                    keyColumns: new[] { "LanguageId", "Token" },
                    keyValues: new object[] { 37L, token });
        }
    }
}
