using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameDaypartTokens : Migration
    {
        // WX-265: rationalize the daypart vocabulary to stable ordinal keys. The three named
        // daypart tokens (PartMorning/PartAfternoon/PartEvening) become DayPart2/3/4, and the
        // 00:00-06:00 block — which never had a token — gets DayPart1 ("early hours"). Renaming
        // the persisted Token keys (rather than editing the historical seed migration, which has
        // already applied to production DBs) keeps this forward-only and idempotent per DB. Both
        // tables that carry the keys are renamed together: LanguageTemplates (the per-language
        // phrase rows) and PromptGlossaryTokens (the WX-244 language-neutral anchor registry) —
        // the latter is validated fail-closed against the Tok contract at load, so a missed rename
        // there would silently drop daypart anchoring. DayPart1 is seeded here but stays clock-
        // bound in deterministic prose (WX-190); WX-264 decides how the narrative consumes it.
        //
        // Data-only (no schema change → no SchemaVersion bump). The test-side SeedTemplateStore
        // parses the RenameToken(...) calls below to keep TokSeedParityTests green without touching
        // the historical seed migration; keep the RenameToken(mb, "old", "new") shape it recognizes.

        // en (LanguageId 37L, WX-166 ISO-seed) is the only seeded language (WX-251). The DayPart1
        // InsertData row below keeps the literal 37L the historical seed uses so the test-side
        // SeedTemplateStore parser (which keys on "37L,") picks it up for the parity gate.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RenameToken(migrationBuilder, "PartMorning", "DayPart2");
            RenameToken(migrationBuilder, "PartAfternoon", "DayPart3");
            RenameToken(migrationBuilder, "PartEvening", "DayPart4");

            migrationBuilder.InsertData(
                table: "LanguageTemplates",
                columns: new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" },
                values: new object[] { 37L, "DayPart1", "early hours", "Name of the 00:00-06:00 civil daypart — the pre-dawn block between midnight and roughly sunrise. Supply the target language's own natural period-of-day term for this window (e.g. Spanish 'madrugada', German 'frühe Morgenstunden'); do not literally translate the English phrase. Note that English has no clean single word for this block — we chose 'early hours' over 'predawn' / 'small hours' as the least-bad fit, so your language may face the same gap. If it does, pick the best available term AND record your uncertainty plus one or two alternatives in the note field so a human can research it. If the concept genuinely can't be expressed, mark it not representable.", "Hint", true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Delete DayPart1 across ALL languages, not just the en seed row: WX-250 top-up creates
            // DayPart1 rows for the enabled targets at runtime, so an en-only delete would orphan
            // them. Mirrors the all-language scope of the rename reversal below.
            migrationBuilder.DeleteData(
                table: "LanguageTemplates",
                keyColumn: "Token",
                keyValue: "DayPart1");

            RenameToken(migrationBuilder, "DayPart4", "PartEvening");
            RenameToken(migrationBuilder, "DayPart3", "PartAfternoon");
            RenameToken(migrationBuilder, "DayPart2", "PartMorning");
        }

        // Renames a vocabulary token key in both tables that persist it: the per-language phrase
        // rows (LanguageTemplates) and the language-neutral anchor registry (PromptGlossaryTokens).
        // Renaming in place (not delete+insert) preserves row identity/history. A token absent from
        // a table is a harmless no-op there.
        private static void RenameToken(MigrationBuilder migrationBuilder, string oldToken, string newToken)
        {
            migrationBuilder.UpdateData(
                table: "LanguageTemplates",
                keyColumn: "Token",
                keyValue: oldToken,
                column: "Token",
                value: newToken);

            migrationBuilder.UpdateData(
                table: "PromptGlossaryTokens",
                keyColumn: "Token",
                keyValue: oldToken,
                column: "Token",
                value: newToken);
        }
    }
}
