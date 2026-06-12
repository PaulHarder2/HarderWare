using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLanguagesTableAndRecipientFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Languages registry (WX-166): ISO 639-1 "AllLanguages" seed ──────────
            migrationBuilder.CreateTable(
                name: "Languages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IsoCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Languages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Languages_IsoCode",
                table: "Languages",
                column: "IsoCode",
                unique: true);

            // Seed the full ISO 639-1 list; only en + es are enabled ("SupportedLanguages")
            // — the languages the renderer currently has templates for (WX-166). Identity
            // assigns Id; the FK backfill below resolves rows by IsoCode, not by Id.
            migrationBuilder.InsertData(
                table: "Languages",
                columns: new[] { "IsoCode", "DisplayName", "IsEnabled" },
                values: new object[,]
                {
                { "aa", "Afar", false },
                { "ab", "Abkhazian", false },
                { "ae", "Avestan", false },
                { "af", "Afrikaans", false },
                { "ak", "Akan", false },
                { "am", "Amharic", false },
                { "an", "Aragonese", false },
                { "ar", "Arabic", false },
                { "as", "Assamese", false },
                { "av", "Avaric", false },
                { "ay", "Aymara", false },
                { "az", "Azerbaijani", false },
                { "ba", "Bashkir", false },
                { "be", "Belarusian", false },
                { "bg", "Bulgarian", false },
                { "bi", "Bislama", false },
                { "bm", "Bambara", false },
                { "bn", "Bengali", false },
                { "bo", "Tibetan", false },
                { "br", "Breton", false },
                { "bs", "Bosnian", false },
                { "ca", "Catalan", false },
                { "ce", "Chechen", false },
                { "ch", "Chamorro", false },
                { "co", "Corsican", false },
                { "cr", "Cree", false },
                { "cs", "Czech", false },
                { "cu", "Church Slavic", false },
                { "cv", "Chuvash", false },
                { "cy", "Welsh", false },
                { "da", "Danish", false },
                { "de", "German", false },
                { "dv", "Divehi", false },
                { "dz", "Dzongkha", false },
                { "ee", "Ewe", false },
                { "el", "Modern Greek (1453-)", false },
                { "en", "English", true },
                { "eo", "Esperanto", false },
                { "es", "Spanish", true },
                { "et", "Estonian", false },
                { "eu", "Basque", false },
                { "fa", "Persian", false },
                { "ff", "Fulah", false },
                { "fi", "Finnish", false },
                { "fj", "Fijian", false },
                { "fo", "Faroese", false },
                { "fr", "French", false },
                { "fy", "Western Frisian", false },
                { "ga", "Irish", false },
                { "gd", "Scottish Gaelic", false },
                { "gl", "Galician", false },
                { "gn", "Guarani", false },
                { "gu", "Gujarati", false },
                { "gv", "Manx", false },
                { "ha", "Hausa", false },
                { "he", "Hebrew", false },
                { "hi", "Hindi", false },
                { "ho", "Hiri Motu", false },
                { "hr", "Croatian", false },
                { "ht", "Haitian", false },
                { "hu", "Hungarian", false },
                { "hy", "Armenian", false },
                { "hz", "Herero", false },
                { "ia", "Interlingua (International Auxiliary Language Association)", false },
                { "id", "Indonesian", false },
                { "ie", "Interlingue", false },
                { "ig", "Igbo", false },
                { "ii", "Sichuan Yi", false },
                { "ik", "Inupiaq", false },
                { "io", "Ido", false },
                { "is", "Icelandic", false },
                { "it", "Italian", false },
                { "iu", "Inuktitut", false },
                { "ja", "Japanese", false },
                { "jv", "Javanese", false },
                { "ka", "Georgian", false },
                { "kg", "Kongo", false },
                { "ki", "Kikuyu", false },
                { "kj", "Kuanyama", false },
                { "kk", "Kazakh", false },
                { "kl", "Kalaallisut", false },
                { "km", "Khmer", false },
                { "kn", "Kannada", false },
                { "ko", "Korean", false },
                { "kr", "Kanuri", false },
                { "ks", "Kashmiri", false },
                { "ku", "Kurdish", false },
                { "kv", "Komi", false },
                { "kw", "Cornish", false },
                { "ky", "Kirghiz", false },
                { "la", "Latin", false },
                { "lb", "Luxembourgish", false },
                { "lg", "Ganda", false },
                { "li", "Limburgan", false },
                { "ln", "Lingala", false },
                { "lo", "Lao", false },
                { "lt", "Lithuanian", false },
                { "lu", "Luba-Katanga", false },
                { "lv", "Latvian", false },
                { "mg", "Malagasy", false },
                { "mh", "Marshallese", false },
                { "mi", "Maori", false },
                { "mk", "Macedonian", false },
                { "ml", "Malayalam", false },
                { "mn", "Mongolian", false },
                { "mr", "Marathi", false },
                { "ms", "Malay (macrolanguage)", false },
                { "mt", "Maltese", false },
                { "my", "Burmese", false },
                { "na", "Nauru", false },
                { "nb", "Norwegian Bokmål", false },
                { "nd", "North Ndebele", false },
                { "ne", "Nepali (macrolanguage)", false },
                { "ng", "Ndonga", false },
                { "nl", "Dutch", false },
                { "nn", "Norwegian Nynorsk", false },
                { "no", "Norwegian", false },
                { "nr", "South Ndebele", false },
                { "nv", "Navajo", false },
                { "ny", "Chichewa", false },
                { "oc", "Occitan (post 1500)", false },
                { "oj", "Ojibwa", false },
                { "om", "Oromo", false },
                { "or", "Oriya (macrolanguage)", false },
                { "os", "Ossetian", false },
                { "pa", "Panjabi", false },
                { "pi", "Pali", false },
                { "pl", "Polish", false },
                { "ps", "Pushto", false },
                { "pt", "Portuguese", false },
                { "qu", "Quechua", false },
                { "rm", "Romansh", false },
                { "rn", "Rundi", false },
                { "ro", "Romanian", false },
                { "ru", "Russian", false },
                { "rw", "Kinyarwanda", false },
                { "sa", "Sanskrit", false },
                { "sc", "Sardinian", false },
                { "sd", "Sindhi", false },
                { "se", "Northern Sami", false },
                { "sg", "Sango", false },
                { "sh", "Serbo-Croatian", false },
                { "si", "Sinhala", false },
                { "sk", "Slovak", false },
                { "sl", "Slovenian", false },
                { "sm", "Samoan", false },
                { "sn", "Shona", false },
                { "so", "Somali", false },
                { "sq", "Albanian", false },
                { "sr", "Serbian", false },
                { "ss", "Swati", false },
                { "st", "Southern Sotho", false },
                { "su", "Sundanese", false },
                { "sv", "Swedish", false },
                { "sw", "Swahili (macrolanguage)", false },
                { "ta", "Tamil", false },
                { "te", "Telugu", false },
                { "tg", "Tajik", false },
                { "th", "Thai", false },
                { "ti", "Tigrinya", false },
                { "tk", "Turkmen", false },
                { "tl", "Tagalog", false },
                { "tn", "Tswana", false },
                { "to", "Tonga (Tonga Islands)", false },
                { "tr", "Turkish", false },
                { "ts", "Tsonga", false },
                { "tt", "Tatar", false },
                { "tw", "Twi", false },
                { "ty", "Tahitian", false },
                { "ug", "Uighur", false },
                { "uk", "Ukrainian", false },
                { "ur", "Urdu", false },
                { "uz", "Uzbek", false },
                { "ve", "Venda", false },
                { "vi", "Vietnamese", false },
                { "vo", "Volapük", false },
                { "wa", "Walloon", false },
                { "wo", "Wolof", false },
                { "xh", "Xhosa", false },
                { "yi", "Yiddish", false },
                { "yo", "Yoruba", false },
                { "za", "Zhuang", false },
                { "zh", "Chinese", false },
                { "zu", "Zulu", false },
                });

            // ── Recipients.Language (string) → Recipients.LanguageId (FK) ───────────
            migrationBuilder.AddColumn<long>(
                name: "LanguageId",
                table: "Recipients",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipients_LanguageId",
                table: "Recipients",
                column: "LanguageId");

            migrationBuilder.AddForeignKey(
                name: "FK_Recipients_Languages_LanguageId",
                table: "Recipients",
                column: "LanguageId",
                principalTable: "Languages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Backfill the FK from the old free-text values. That column was a free-text
            // TextBox with no normalization at the entry point, so map the two canonical
            // values explicitly — trimmed, and case-folded by SQL Server's default CI
            // collation — resolving by IsoCode (the identity Ids aren't known here). A
            // NULL/blank Language is intentional (→ the service default language).
            migrationBuilder.Sql(
                "UPDATE Recipients SET LanguageId = (SELECT Id FROM Languages WHERE IsoCode = 'en') WHERE LTRIM(RTRIM(Language)) = 'English';");
            migrationBuilder.Sql(
                "UPDATE Recipients SET LanguageId = (SELECT Id FROM Languages WHERE IsoCode = 'es') WHERE LTRIM(RTRIM(Language)) = 'Spanish';");

            // Safety: do NOT silently lose data. If any non-blank Language value did not
            // map (an un-normalized stored value the two UPDATEs missed — e.g. "Español"),
            // abort the migration BEFORE the irreversible column drop. A loud failure to
            // investigate beats a recipient silently reverting to the default language.
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM Recipients WHERE Language IS NOT NULL AND LTRIM(RTRIM(Language)) <> '' AND LanguageId IS NULL) " +
                "THROW 50000, 'WX-166 backfill: a Recipients.Language value did not map to a LanguageId (un-normalized value). " +
                "Normalize it to a seeded language name and re-run; aborting before the column drop.', 1;");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "Recipients");

            // Unrelated pre-existing model drift folded in (documented on WX-166): the
            // ForecastSnapshots.SchemaVersion default advanced 4→5 without its own
            // migration. Cosmetic — the column is always written explicitly from
            // ForecastSnapshotBody.SchemaVersionCurrent, never relying on the default.
            migrationBuilder.AlterColumn<int>(
                name: "SchemaVersion",
                table: "ForecastSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 5,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "SchemaVersion",
                table: "ForecastSnapshots",
                type: "int",
                nullable: false,
                defaultValue: 4,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 5);

            // Restore the free-text Language column from the FK before dropping the FK.
            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "Recipients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
            migrationBuilder.Sql(
                "UPDATE Recipients SET Language = (SELECT DisplayName FROM Languages WHERE Id = Recipients.LanguageId) WHERE LanguageId IS NOT NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_Recipients_Languages_LanguageId",
                table: "Recipients");

            migrationBuilder.DropIndex(
                name: "IX_Recipients_LanguageId",
                table: "Recipients");

            migrationBuilder.DropColumn(
                name: "LanguageId",
                table: "Recipients");

            migrationBuilder.DropTable(
                name: "Languages");
        }
    }
}