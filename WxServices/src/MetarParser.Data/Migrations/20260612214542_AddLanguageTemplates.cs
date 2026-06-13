using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLanguageTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CultureName",
                table: "Languages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GeneratedAtUtc",
                table: "Languages",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GenerationError",
                table: "Languages",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LanguageTemplates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LanguageId = table.Column<long>(type: "bigint", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Phrase = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ContextInfo = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ContextKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Representable = table.Column<bool>(type: "bit", nullable: false),
                    ReviewedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LanguageTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LanguageTemplates_Languages_LanguageId",
                        column: x => x.LanguageId,
                        principalTable: "Languages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_LanguageTemplates_LanguageId_Token",
                table: "LanguageTemplates",
                columns: new[] { "LanguageId", "Token" },
                unique: true);

            // ── WX-171 dormant seed ──────────────────────────────────────────
            // Seeds en (LanguageId 37) + es (39) from the WX-166 ISO registry.
            // The renderer still reads the hard-coded ReportVocabulary at this
            // point, so these rows are inert until WX-171's wire step. Ids are
            // deterministic from WX-166's seed; guard fail-closed if that drifts.
            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 37 AND IsoCode = 'en') " +
                "OR NOT EXISTS (SELECT 1 FROM Languages WHERE Id = 39 AND IsoCode = 'es') " +
                "THROW 50000, 'WX-171 seed: Languages Id 37/39 are not en/es as expected (WX-166 ISO-seed drift). Aborting before seeding templates.', 1;");

            migrationBuilder.UpdateData(table: "Languages", keyColumn: "Id", keyValue: 37L, column: "CultureName", value: "en-US");
            migrationBuilder.UpdateData(table: "Languages", keyColumn: "Id", keyValue: 39L, column: "CultureName", value: "es-US");

            var cols = new[] { "LanguageId", "Token", "Phrase", "ContextInfo", "ContextKind", "Representable" };

            migrationBuilder.InsertData(table: "LanguageTemplates", columns: cols, values: new object[,]
            {
                // ── en (LanguageId 37) ──
                { 37L, "CurrentConditionsHeading", "Current Conditions", "Section heading above the latest-observation block.", "Hint", true },
                { 37L, "WhatsChangedLabel", "What's changed:", "Inline label introducing the list of changes since last report.", "Hint", true },
                { 37L, "InSummaryLabel", "In summary:", "Inline label introducing the closing one-line summary.", "Hint", true },
                { 37L, "NoObservationNote", "No recent observation is available from any nearby station, so the report below is based on forecast model data only.", "No recent observation is available from any nearby station, so the report below is based on forecast model data only.", "Example", true },
                { 37L, "ScheduledReportLabel", "Scheduled Report", "Header label for a routine scheduled report.", "Hint", true },
                { 37L, "ScheduledReportSubject", "Weather Report", "Email-subject noun for a scheduled report.", "Hint", true },
                { 37L, "UnscheduledUpdateLabel", "Unscheduled Update", "Header label for an off-schedule update triggered by changing conditions.", "Hint", true },
                { 37L, "UnscheduledUpdateSubject", "Weather Update", "Email-subject noun for an unscheduled update.", "Hint", true },
                { 37L, "DiagnosticLabel", "Diagnostic", "Header label for a post-deploy diagnostic/test report.", "Hint", true },
                { 37L, "DiagnosticSubject", "Diagnostic", "Email-subject noun for a diagnostic report.", "Hint", true },
                { 37L, "SubjectForConnective", "for", "Preposition joining the report name to the recipient's name, as in 'Weather Report __ Maria'.", "Hint", true },
                { 37L, "WelcomeSubject", "Welcome to WxReport", "Subject of the first-time welcome email ('WxReport' is a product name; keep as-is).", "Hint", true },
                { 37L, "WelcomeGreeting", "Welcome to WxReport!", "Welcome to WxReport!", "Example", true },
                { 37L, "AndConjunction", "and", "Conjunction joining the last two scheduled send times: '6 a.m. __ 6 p.m.'", "Hint", true },
                { 37L, "RowSky", "Sky", "Table row label for cloud cover.", "Hint", true },
                { 37L, "RowVisibility", "Visibility", "Table row label for how far one can see.", "Hint", true },
                { 37L, "RowWind", "Wind", "Table row label for wind direction and speed.", "Hint", true },
                { 37L, "RowWeather", "Weather", "Table row label for present weather (rain/snow/fog).", "Hint", true },
                { 37L, "RowTemperature", "Temperature", "Table row label for the current temperature.", "Hint", true },
                { 37L, "RowHumidity", "Relative Humidity", "Table row label for relative humidity (percent).", "Hint", true },
                { 37L, "RowPressure", "Pressure", "Table row label for barometric (atmospheric) pressure, not stress/force.", "Hint", true },
                { 37L, "ColDate", "Date", "Column header for the date in the multi-day forecast.", "Hint", true },
                { 37L, "ColTemperatures", "Temperatures", "Column header for each day's high/low.", "Hint", true },
                { 37L, "ColWind", "Wind", "Column header for each day's wind.", "Hint", true },
                { 37L, "ColConditions", "Conditions", "Column header for each day's summary.", "Hint", true },
                { 37L, "HighLabel", "High", "Abbreviated label for the daily maximum temperature ('High: 34 deg').", "Hint", true },
                { 37L, "LowLabel", "Low", "Abbreviated label for the daily minimum temperature ('Low: 24 deg').", "Hint", true },
                { 37L, "SkyClear", "Clear", "Sky-cover term: a cloudless sky.", "Hint", true },
                { 37L, "SkyPartlyCloudy", "Partly cloudy", "Sky-cover term: scattered clouds, more sky than cloud.", "Hint", true },
                { 37L, "SkyMostlyCloudy", "Mostly cloudy", "Sky-cover term: more cloud than sky.", "Hint", true },
                { 37L, "SkyOvercast", "Overcast", "Sky-cover term: a complete cloud deck.", "Hint", true },
                { 37L, "SkyObscured", "Sky obscured", "Sky-cover term: sky hidden by fog/smoke.", "Hint", true },
                { 37L, "VisGood", "Good", "Visibility band: clear, six miles or more.", "Hint", true },
                { 37L, "VisHazy", "Hazy", "Visibility band: hazy, two to six miles.", "Hint", true },
                { 37L, "VisReduced", "Reduced", "Visibility band: reduced, under two miles.", "Hint", true },
                { 37L, "VisPoor", "Poor", "Visibility band: poor, under half a mile.", "Hint", true },
                { 37L, "WindCalm", "Calm", "Wind term: essentially no wind.", "Hint", true },
                { 37L, "WindVariable", "Variable", "Wind term: light wind, no steady direction.", "Hint", true },
                { 37L, "WxFog", "Fog", "Obscuration: fog (dense water droplets, visibility under ~1 km).", "Hint", true },
                { 37L, "WxMist", "Mist", "Obscuration: mist (thin humid haze; lighter than fog).", "Hint", true },
                { 37L, "WxHaze", "Haze", "Obscuration: haze (humidity/particulate dimming, not dust-storm calima).", "Hint", true },
                { 37L, "WxSmoke", "Smoke", "Obscuration: smoke from fires.", "Hint", true },
                { 37L, "PartMorning", "Morning", "Day-part label: morning hours (sunrise to noon).", "Hint", true },
                { 37L, "PartAfternoon", "Afternoon", "Day-part label: afternoon (noon to ~5 pm).", "Hint", true },
                { 37L, "PartEvening", "Evening", "Day-part label: evening (sunset to ~11 pm), not late-night noche/madrugada.", "Hint", true },
                { 37L, "PartOvernight", "Overnight", "Day-part label: overnight hours after midnight.", "Hint", true },
                { 37L, "CondMorning", "in the morning", "Time adverbial: during the morning (sunrise to noon).", "Hint", true },
                { 37L, "CondAfternoon", "in the afternoon", "Time adverbial: during the afternoon (noon to ~5 pm).", "Hint", true },
                { 37L, "CondEvening", "in the evening", "Time adverbial: during the evening (sunset to ~11 pm), not late night.", "Hint", true },
                { 37L, "CondOvernight", "overnight", "Time adverbial: through the overnight hours after midnight.", "Hint", true },
                { 37L, "Storms", "storms", "Scattered storms in the afternoon.", "Example", true },
                { 37L, "ForecastHeadingFormat", "Forecast for {0}", "Forecast for Spring, TX", "Example", true },
                { 37L, "WelcomeFormat", "From now on you'll receive a daily weather report for {0} at {1} local time, plus extra alerts whenever the weather changes significantly. We're glad to have you.", "From now on you'll receive a daily report for Spring, TX at 6 a.m. local time.", "Example", true },
                { 37L, "HazardBannerFormat", "{0} in your forecast — {1}.", "Severe storms in your forecast — Saturday afternoon.", "Example", true },
                { 37L, "WindLine", "{0} at {1}", "Template for 'direction at speed' (e.g. NW at 15 mph); the 'at' marks a rate. Give the language's template; {0}=dir, {1}=speed.", "Hint", true },
                { 37L, "WindGust", ", gusting {0}", "Appended gust clause (', gusting 30 mph'); keep {0}=gust speed.", "Hint", true },
                { 37L, "StationSubtitle", "at {0}", "Subtitle 'at <station>'; the 'at' marks a location. Keep {0}=station.", "Hint", true },
                { 37L, "SevereClause", "{0} {1}", "Severe clause = [noun+outlook phrase] then [time-of-day word]; give the language's slot order, keep {0},{1}.", "Hint", true },
                { 37L, "EpisodeLine", "{0} — {1}", "Per-day line = [day-part/range] — [phenomenon+outlook phrase]; keep {0},{1}.", "Hint", true },
                { 37L, "EpisodeRange", "{0}–{1}", "A span of two day-parts ('Overnight-morning'); keep {0},{1}.", "Hint", true },
                { 37L, "ClauseJoin", ", then ", "Connective joining chronological clauses ('...afternoon, then rain...').", "Hint", true },
                { 37L, "rain", "rain", "Steady rain was falling at the time of the observation.", "Example", true },
                { 37L, "rain_light", "light rain", "Light rain was falling, under a tenth of an inch per hour.", "Example", true },
                { 37L, "rain_heavy", "heavy rain", "Heavy rain was coming down in sheets.", "Example", true },
                { 37L, "rain_freezing", "freezing rain", "Freezing rain was glazing the roads.", "Example", true },
                { 37L, "rain_showers", "rain showers", "Brief rain showers were passing through.", "Example", true },
                { 37L, "drizzle", "drizzle", "A fine drizzle was misting the windshield.", "Example", true },
                { 37L, "drizzle_light", "light drizzle", "Light drizzle was barely dampening the pavement.", "Example", true },
                { 37L, "drizzle_freezing", "freezing drizzle", "Freezing drizzle was leaving a thin glaze on cars.", "Example", true },
                { 37L, "snow", "snow", "Snow was falling and beginning to stick.", "Example", true },
                { 37L, "snow_light", "light snow", "Light snow was dusting the grass.", "Example", true },
                { 37L, "snow_heavy", "heavy snow", "Heavy snow was piling up fast.", "Example", true },
                { 37L, "snow_showers", "snow showers", "Passing snow showers were bringing brief whiteouts.", "Example", true },
                { 37L, "wintry_mix", "wintry mix", "A wintry mix of sleet and ice pellets was falling.", "Example", true },
                { 37L, "storms_possible", "storms possible", "Scattered storms are possible in the afternoon.", "Example", true },
                { 37L, "storms_likely", "storms likely", "Storms are likely by late afternoon.", "Example", true },
                { 37L, "storms_expected", "storms expected", "Storms are expected Saturday afternoon.", "Example", true },
                { 37L, "rain_possible", "rain possible", "Rain is possible in the morning.", "Example", true },
                { 37L, "rain_likely", "rain likely", "Rain is likely this afternoon.", "Example", true },
                { 37L, "rain_expected", "rain expected", "Rain is expected overnight.", "Example", true },
                { 37L, "snow_possible", "snow possible", "Snow is possible in the hills.", "Example", true },
                { 37L, "snow_likely", "snow likely", "Snow is likely by morning.", "Example", true },
                { 37L, "snow_expected", "snow expected", "Snow is expected overnight.", "Example", true },
                { 37L, "wmix_possible", "wintry mix possible", "A wintry mix is possible near dawn.", "Example", true },
                { 37L, "wmix_likely", "wintry mix likely", "A wintry mix is likely overnight.", "Example", true },
                { 37L, "wmix_expected", "wintry mix expected", "A wintry mix is expected before sunrise.", "Example", true },
                { 37L, "fzra_possible", "freezing rain possible", "Freezing rain is possible at daybreak.", "Example", true },
                { 37L, "fzra_likely", "freezing rain likely", "Freezing rain is likely overnight.", "Example", true },
                { 37L, "fzra_expected", "freezing rain expected", "Freezing rain is expected before dawn.", "Example", true },
                { 37L, "sev_storms_possible", "Severe storms possible", "Severe storms are possible in the afternoon.", "Example", true },
                { 37L, "sev_storms_likely", "Severe storms likely", "Severe storms are likely by evening.", "Example", true },
                { 37L, "sev_storms_expected", "Severe storms expected", "Severe storms are expected Saturday.", "Example", true },
                { 37L, "sev_wx_possible", "Severe weather possible", "Severe weather is possible, including damaging wind.", "Example", true },
                { 37L, "sev_wx_likely", "Severe weather likely", "Severe weather is likely in the afternoon.", "Example", true },
                { 37L, "sev_wx_expected", "Severe weather expected", "Severe weather is expected later today.", "Example", true },
                { 37L, "sky_overcast_low", "low overcast", "A low overcast hung over the bay.", "Example", true },
                { 37L, "sky_overcast_high", "high overcast", "A high overcast dimmed the sun.", "Example", true },
                { 37L, "sky_mostlycloudy_low", "low mostly cloudy", "Skies were mostly cloudy with a low cloud base.", "Example", true },
                { 37L, "sky_mostlycloudy_high", "high mostly cloudy", "Skies were mostly cloudy with high, thin clouds.", "Example", true },
                { 37L, "clear_and_dry", "clear and dry", "The day stays clear and dry.", "Example", true },

                // ── es (LanguageId 39) — hint contexts keep the English gloss; example contexts translated ──
                { 39L, "CurrentConditionsHeading", "Condiciones actuales", "Section heading above the latest-observation block.", "Hint", true },
                { 39L, "WhatsChangedLabel", "Qué ha cambiado:", "Inline label introducing the list of changes since last report.", "Hint", true },
                { 39L, "InSummaryLabel", "En resumen:", "Inline label introducing the closing one-line summary.", "Hint", true },
                { 39L, "NoObservationNote", "No hay una observación reciente de ninguna estación cercana, por lo que el informe a continuación se basa únicamente en datos del modelo de pronóstico.", "No hay una observación reciente de ninguna estación cercana, por lo que el informe a continuación se basa únicamente en datos del modelo de pronóstico.", "Example", true },
                { 39L, "ScheduledReportLabel", "Reporte programado", "Header label for a routine scheduled report.", "Hint", true },
                { 39L, "ScheduledReportSubject", "Reporte del tiempo", "Email-subject noun for a scheduled report.", "Hint", true },
                { 39L, "UnscheduledUpdateLabel", "Actualización no programada", "Header label for an off-schedule update triggered by changing conditions.", "Hint", true },
                { 39L, "UnscheduledUpdateSubject", "Actualización del tiempo", "Email-subject noun for an unscheduled update.", "Hint", true },
                { 39L, "DiagnosticLabel", "Diagnóstico", "Header label for a post-deploy diagnostic/test report.", "Hint", true },
                { 39L, "DiagnosticSubject", "Diagnóstico", "Email-subject noun for a diagnostic report.", "Hint", true },
                { 39L, "SubjectForConnective", "para", "Preposition joining the report name to the recipient's name, as in 'Weather Report __ Maria'.", "Hint", true },
                { 39L, "WelcomeSubject", "Bienvenido a WxReport", "Subject of the first-time welcome email ('WxReport' is a product name; keep as-is).", "Hint", true },
                { 39L, "WelcomeGreeting", "¡Bienvenido a WxReport!", "¡Bienvenido a WxReport!", "Example", true },
                { 39L, "AndConjunction", "y", "Conjunction joining the last two scheduled send times: '6 a.m. __ 6 p.m.'", "Hint", true },
                { 39L, "RowSky", "Cielo", "Table row label for cloud cover.", "Hint", true },
                { 39L, "RowVisibility", "Visibilidad", "Table row label for how far one can see.", "Hint", true },
                { 39L, "RowWind", "Viento", "Table row label for wind direction and speed.", "Hint", true },
                { 39L, "RowWeather", "Tiempo", "Table row label for present weather (rain/snow/fog).", "Hint", true },
                { 39L, "RowTemperature", "Temperatura", "Table row label for the current temperature.", "Hint", true },
                { 39L, "RowHumidity", "Humedad relativa", "Table row label for relative humidity (percent).", "Hint", true },
                { 39L, "RowPressure", "Presión", "Table row label for barometric (atmospheric) pressure, not stress/force.", "Hint", true },
                { 39L, "ColDate", "Fecha", "Column header for the date in the multi-day forecast.", "Hint", true },
                { 39L, "ColTemperatures", "Temperaturas", "Column header for each day's high/low.", "Hint", true },
                { 39L, "ColWind", "Viento", "Column header for each day's wind.", "Hint", true },
                { 39L, "ColConditions", "Condiciones", "Column header for each day's summary.", "Hint", true },
                { 39L, "HighLabel", "Máx", "Abbreviated label for the daily maximum temperature ('High: 34 deg').", "Hint", true },
                { 39L, "LowLabel", "Mín", "Abbreviated label for the daily minimum temperature ('Low: 24 deg').", "Hint", true },
                { 39L, "SkyClear", "Despejado", "Sky-cover term: a cloudless sky.", "Hint", true },
                { 39L, "SkyPartlyCloudy", "Parcialmente nublado", "Sky-cover term: scattered clouds, more sky than cloud.", "Hint", true },
                { 39L, "SkyMostlyCloudy", "Mayormente nublado", "Sky-cover term: more cloud than sky.", "Hint", true },
                { 39L, "SkyOvercast", "Nublado", "Sky-cover term: a complete cloud deck.", "Hint", true },
                { 39L, "SkyObscured", "Cielo cubierto", "Sky-cover term: sky hidden by fog/smoke.", "Hint", true },
                { 39L, "VisGood", "Buena", "Visibility band: clear, six miles or more.", "Hint", true },
                { 39L, "VisHazy", "Brumosa", "Visibility band: hazy, two to six miles.", "Hint", true },
                { 39L, "VisReduced", "Reducida", "Visibility band: reduced, under two miles.", "Hint", true },
                { 39L, "VisPoor", "Mala", "Visibility band: poor, under half a mile.", "Hint", true },
                { 39L, "WindCalm", "Calma", "Wind term: essentially no wind.", "Hint", true },
                { 39L, "WindVariable", "Variable", "Wind term: light wind, no steady direction.", "Hint", true },
                { 39L, "WxFog", "Niebla", "Obscuration: fog (dense water droplets, visibility under ~1 km).", "Hint", true },
                { 39L, "WxMist", "Neblina", "Obscuration: mist (thin humid haze; lighter than fog).", "Hint", true },
                { 39L, "WxHaze", "Calima", "Obscuration: haze (humidity/particulate dimming, not dust-storm calima).", "Hint", true },
                { 39L, "WxSmoke", "Humo", "Obscuration: smoke from fires.", "Hint", true },
                { 39L, "PartMorning", "Mañana", "Day-part label: morning hours (sunrise to noon).", "Hint", true },
                { 39L, "PartAfternoon", "Tarde", "Day-part label: afternoon (noon to ~5 pm).", "Hint", true },
                { 39L, "PartEvening", "Noche", "Day-part label: evening (sunset to ~11 pm), not late-night noche/madrugada.", "Hint", true },
                { 39L, "PartOvernight", "Madrugada", "Day-part label: overnight hours after midnight.", "Hint", true },
                { 39L, "CondMorning", "por la mañana", "Time adverbial: during the morning (sunrise to noon).", "Hint", true },
                { 39L, "CondAfternoon", "por la tarde", "Time adverbial: during the afternoon (noon to ~5 pm).", "Hint", true },
                { 39L, "CondEvening", "al anochecer", "Time adverbial: during the evening (sunset to ~11 pm), not late night.", "Hint", true },
                { 39L, "CondOvernight", "de madrugada", "Time adverbial: through the overnight hours after midnight.", "Hint", true },
                { 39L, "Storms", "tormentas", "Tormentas dispersas por la tarde.", "Example", true },
                { 39L, "ForecastHeadingFormat", "Pronóstico para {0}", "Pronóstico para Spring, TX", "Example", true },
                { 39L, "WelcomeFormat", "A partir de ahora recibirá un informe del tiempo diario para {0} a las {1} hora local, además de alertas adicionales cuando el tiempo cambie significativamente. Nos alegra tenerle con nosotros.", "A partir de ahora recibirá un informe diario para Spring, TX a las 6 a. m. hora local.", "Example", true },
                { 39L, "HazardBannerFormat", "{0} en su pronóstico — {1}.", "Tormentas severas en su pronóstico — el sábado por la tarde.", "Example", true },
                { 39L, "WindLine", "{0} a {1}", "Template for 'direction at speed' (e.g. NW at 15 mph); the 'at' marks a rate. Give the language's template; {0}=dir, {1}=speed.", "Hint", true },
                { 39L, "WindGust", ", con ráfagas de {0}", "Appended gust clause (', gusting 30 mph'); keep {0}=gust speed.", "Hint", true },
                { 39L, "StationSubtitle", "en {0}", "Subtitle 'at <station>'; the 'at' marks a location. Keep {0}=station.", "Hint", true },
                { 39L, "SevereClause", "{0} {1}", "Severe clause = [noun+outlook phrase] then [time-of-day word]; give the language's slot order, keep {0},{1}.", "Hint", true },
                { 39L, "EpisodeLine", "{0} — {1}", "Per-day line = [day-part/range] — [phenomenon+outlook phrase]; keep {0},{1}.", "Hint", true },
                { 39L, "EpisodeRange", "{0}–{1}", "A span of two day-parts ('Overnight-morning'); keep {0},{1}.", "Hint", true },
                { 39L, "ClauseJoin", ", luego ", "Connective joining chronological clauses ('...afternoon, then rain...').", "Hint", true },
                { 39L, "rain", "lluvia", "Caía lluvia constante en el momento de la observación.", "Example", true },
                { 39L, "rain_light", "lluvia ligera", "Caía lluvia ligera, a menos de 2,5 mm por hora.", "Example", true },
                { 39L, "rain_heavy", "lluvia fuerte", "Caía lluvia fuerte a cántaros.", "Example", true },
                { 39L, "rain_freezing", "lluvia helada", "Lluvia helada cubría las carreteras de hielo.", "Example", true },
                { 39L, "rain_showers", "chubascos de lluvia", "Pasaban breves chubascos de lluvia.", "Example", true },
                { 39L, "drizzle", "llovizna", "Una fina llovizna empañaba el parabrisas.", "Example", true },
                { 39L, "drizzle_light", "llovizna ligera", "Llovizna ligera apenas humedecía el pavimento.", "Example", true },
                { 39L, "drizzle_freezing", "llovizna helada", "Llovizna helada dejaba una fina capa de hielo en los autos.", "Example", true },
                { 39L, "snow", "nieve", "Caía nieve y empezaba a cuajar.", "Example", true },
                { 39L, "snow_light", "nieve ligera", "Nieve ligera espolvoreaba el césped.", "Example", true },
                { 39L, "snow_heavy", "nieve fuerte", "Nieve fuerte se acumulaba rápidamente.", "Example", true },
                { 39L, "snow_showers", "chubascos de nieve", "Chubascos de nieve pasajeros traían breves ventiscas.", "Example", true },
                { 39L, "wintry_mix", "mezcla invernal", "Caía una mezcla invernal de aguanieve y gránulos de hielo.", "Example", true },
                { 39L, "storms_possible", "tormentas posibles", "Tormentas dispersas son posibles por la tarde.", "Example", true },
                { 39L, "storms_likely", "tormentas probables", "Tormentas son probables al final de la tarde.", "Example", true },
                { 39L, "storms_expected", "tormentas previstas", "Tormentas están previstas para el sábado por la tarde.", "Example", true },
                { 39L, "rain_possible", "lluvia posible", "Lluvia es posible por la mañana.", "Example", true },
                { 39L, "rain_likely", "lluvia probable", "Lluvia es probable esta tarde.", "Example", true },
                { 39L, "rain_expected", "lluvia prevista", "Lluvia está prevista para la madrugada.", "Example", true },
                { 39L, "snow_possible", "nieve posible", "Nieve es posible en las colinas.", "Example", true },
                { 39L, "snow_likely", "nieve probable", "Nieve es probable para la mañana.", "Example", true },
                { 39L, "snow_expected", "nieve prevista", "Nieve está prevista para la madrugada.", "Example", true },
                { 39L, "wmix_possible", "mezcla invernal posible", "Mezcla invernal es posible cerca del amanecer.", "Example", true },
                { 39L, "wmix_likely", "mezcla invernal probable", "Mezcla invernal es probable durante la madrugada.", "Example", true },
                { 39L, "wmix_expected", "mezcla invernal prevista", "Mezcla invernal está prevista antes del amanecer.", "Example", true },
                { 39L, "fzra_possible", "lluvia helada posible", "Lluvia helada es posible al amanecer.", "Example", true },
                { 39L, "fzra_likely", "lluvia helada probable", "Lluvia helada es probable durante la madrugada.", "Example", true },
                { 39L, "fzra_expected", "lluvia helada prevista", "Lluvia helada está prevista antes del amanecer.", "Example", true },
                { 39L, "sev_storms_possible", "Tormentas severas posibles", "Tormentas severas son posibles por la tarde.", "Example", true },
                { 39L, "sev_storms_likely", "Tormentas severas probables", "Tormentas severas son probables al anochecer.", "Example", true },
                { 39L, "sev_storms_expected", "Tormentas severas previstas", "Tormentas severas están previstas para el sábado.", "Example", true },
                { 39L, "sev_wx_possible", "Tiempo severo posible", "Tiempo severo es posible, incluyendo vientos dañinos.", "Example", true },
                { 39L, "sev_wx_likely", "Tiempo severo probable", "Tiempo severo es probable por la tarde.", "Example", true },
                { 39L, "sev_wx_expected", "Tiempo severo previsto", "Tiempo severo está previsto más tarde hoy.", "Example", true },
                { 39L, "sky_overcast_low", "nublado bajo", "Un nublado bajo cubría la bahía.", "Example", true },
                { 39L, "sky_overcast_high", "nublado alto", "Un nublado alto atenuaba el sol.", "Example", true },
                { 39L, "sky_mostlycloudy_low", "mayormente nublado bajo", "El cielo estaba mayormente nublado con una base baja.", "Example", true },
                { 39L, "sky_mostlycloudy_high", "mayormente nublado alto", "El cielo estaba mayormente nublado con nubes altas y delgadas.", "Example", true },
                { 39L, "clear_and_dry", "despejado y seco", "El día se mantiene despejado y seco.", "Example", true },
            });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LanguageTemplates");

            migrationBuilder.DropColumn(
                name: "CultureName",
                table: "Languages");

            migrationBuilder.DropColumn(
                name: "GeneratedAtUtc",
                table: "Languages");

            migrationBuilder.DropColumn(
                name: "GenerationError",
                table: "Languages");
        }
    }
}
