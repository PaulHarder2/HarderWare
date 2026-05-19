using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MetarParser.Data.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GfsGrid",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ModelRunUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ForecastHour = table.Column<int>(type: "int", nullable: false),
                    Lat = table.Column<float>(type: "real", nullable: false),
                    Lon = table.Column<float>(type: "real", nullable: false),
                    TmpC = table.Column<float>(type: "real", nullable: true),
                    DwpC = table.Column<float>(type: "real", nullable: true),
                    UGrdMs = table.Column<float>(type: "real", nullable: true),
                    VGrdMs = table.Column<float>(type: "real", nullable: true),
                    PRateKgM2s = table.Column<float>(type: "real", nullable: true),
                    TcdcPct = table.Column<float>(type: "real", nullable: true),
                    CapeJKg = table.Column<float>(type: "real", nullable: true),
                    PrMslPa = table.Column<float>(type: "real", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GfsGrid", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GfsModelRuns",
                columns: table => new
                {
                    ModelRunUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsComplete = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GfsModelRuns", x => x.ModelRunUtc);
                });

            migrationBuilder.CreateTable(
                name: "GlobalSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClaudeApiKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SmtpUsername = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SmtpPassword = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SmtpFromAddress = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Metars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportType = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    StationIcao = table.Column<string>(type: "nchar(4)", fixedLength: true, maxLength: 4, nullable: false),
                    ObservationUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsAuto = table.Column<bool>(type: "bit", nullable: false),
                    IsCorrection = table.Column<bool>(type: "bit", nullable: false),
                    WindDirection = table.Column<int>(type: "int", nullable: true),
                    WindIsVariable = table.Column<bool>(type: "bit", nullable: false),
                    WindSpeed = table.Column<int>(type: "int", nullable: true),
                    WindGust = table.Column<int>(type: "int", nullable: true),
                    WindUnit = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    WindVariableFrom = table.Column<int>(type: "int", nullable: true),
                    WindVariableTo = table.Column<int>(type: "int", nullable: true),
                    VisibilityCavok = table.Column<bool>(type: "bit", nullable: false),
                    VisibilityM = table.Column<int>(type: "int", nullable: true),
                    VisibilityStatuteMiles = table.Column<double>(type: "float", nullable: true),
                    VisibilityLessThan = table.Column<bool>(type: "bit", nullable: false),
                    AirTemperatureCelsius = table.Column<double>(type: "float", nullable: true),
                    DewPointCelsius = table.Column<double>(type: "float", nullable: true),
                    AltimeterValue = table.Column<double>(type: "float", nullable: true),
                    AltimeterUnit = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: true),
                    RawSkyConditions = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RawWeatherPhenomena = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RawRunwayVisualRange = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Remarks = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RawReport = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Metars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Timezone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ScheduledSendHours = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LocalityName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    MetarIcao = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TafIcao = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    TempUnit = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PressureUnit = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    WindSpeedUnit = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipientStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecipientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastScheduledSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUnscheduledSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSnapshotFingerprint = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastMetarIcao = table.Column<string>(type: "nchar(4)", fixedLength: true, maxLength: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipientStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tafs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportType = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    StationIcao = table.Column<string>(type: "nchar(4)", fixedLength: true, maxLength: 4, nullable: false),
                    IssuanceUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidToUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RawReport = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tafs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WxStations",
                columns: table => new
                {
                    IcaoId = table.Column<string>(type: "nchar(4)", fixedLength: true, maxLength: 4, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Municipality = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Lat = table.Column<double>(type: "float", nullable: true),
                    Lon = table.Column<double>(type: "float", nullable: true),
                    ElevationFt = table.Column<double>(type: "float", nullable: true),
                    AlwaysFetchDirect = table.Column<bool>(type: "bit", nullable: true),
                    Region = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RegionCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    RegionAbbr = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CountryCode = table.Column<string>(type: "nchar(2)", fixedLength: true, maxLength: 2, nullable: true),
                    CountryAbbr = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WxStations", x => x.IcaoId);
                });

            migrationBuilder.CreateTable(
                name: "MetarRunwayVisualRanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MetarId = table.Column<int>(type: "int", nullable: false),
                    Runway = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    MeanMeters = table.Column<int>(type: "int", nullable: true),
                    MinMeters = table.Column<int>(type: "int", nullable: true),
                    MaxMeters = table.Column<int>(type: "int", nullable: true),
                    BelowMinimum = table.Column<bool>(type: "bit", nullable: false),
                    AboveMaximum = table.Column<bool>(type: "bit", nullable: false),
                    Trend = table.Column<string>(type: "nvarchar(1)", maxLength: 1, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetarRunwayVisualRanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetarRunwayVisualRanges_Metars_MetarId",
                        column: x => x.MetarId,
                        principalTable: "Metars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetarSkyConditions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MetarId = table.Column<int>(type: "int", nullable: false),
                    Cover = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    HeightFeet = table.Column<int>(type: "int", nullable: true),
                    CloudType = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    IsVerticalVisibility = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetarSkyConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetarSkyConditions_Metars_MetarId",
                        column: x => x.MetarId,
                        principalTable: "Metars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetarWeatherPhenomena",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MetarId = table.Column<int>(type: "int", nullable: false),
                    PhenomenonKind = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Intensity = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Descriptor = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Precipitation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Obscuration = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    OtherPhenomenon = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetarWeatherPhenomena", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetarWeatherPhenomena_Metars_MetarId",
                        column: x => x.MetarId,
                        principalTable: "Metars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TafChangePeriods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TafId = table.Column<int>(type: "int", nullable: false),
                    ChangeType = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ValidToUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WindDirection = table.Column<int>(type: "int", nullable: true),
                    WindIsVariable = table.Column<bool>(type: "bit", nullable: false),
                    WindSpeed = table.Column<int>(type: "int", nullable: true),
                    WindGust = table.Column<int>(type: "int", nullable: true),
                    WindUnit = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    VisibilityCavok = table.Column<bool>(type: "bit", nullable: false),
                    VisibilityM = table.Column<int>(type: "int", nullable: true),
                    VisibilityStatuteMiles = table.Column<double>(type: "float", nullable: true),
                    VisibilityLessThan = table.Column<bool>(type: "bit", nullable: false),
                    RawSkyConditions = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    RawWeather = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TafChangePeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TafChangePeriods_Tafs_TafId",
                        column: x => x.TafId,
                        principalTable: "Tafs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TafChangePeriodSkyConditions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TafChangePeriodId = table.Column<int>(type: "int", nullable: false),
                    Cover = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    HeightFeet = table.Column<int>(type: "int", nullable: true),
                    CloudType = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    IsVerticalVisibility = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TafChangePeriodSkyConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TafChangePeriodSkyConditions_TafChangePeriods_TafChangePeriodId",
                        column: x => x.TafChangePeriodId,
                        principalTable: "TafChangePeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TafChangePeriodWeatherPhenomena",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TafChangePeriodId = table.Column<int>(type: "int", nullable: false),
                    Intensity = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Descriptor = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    Precipitation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Obscuration = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    OtherPhenomenon = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TafChangePeriodWeatherPhenomena", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TafChangePeriodWeatherPhenomena_TafChangePeriods_TafChangePeriodId",
                        column: x => x.TafChangePeriodId,
                        principalTable: "TafChangePeriods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GfsGrid_Run_Hour",
                table: "GfsGrid",
                columns: new[] { "ModelRunUtc", "ForecastHour" });

            migrationBuilder.CreateIndex(
                name: "UX_GfsGrid_Run_Hour_LatLon",
                table: "GfsGrid",
                columns: new[] { "ModelRunUtc", "ForecastHour", "Lat", "Lon" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetarRunwayVisualRanges_MetarId",
                table: "MetarRunwayVisualRanges",
                column: "MetarId");

            migrationBuilder.CreateIndex(
                name: "IX_Metars_StationIcao",
                table: "Metars",
                column: "StationIcao");

            migrationBuilder.CreateIndex(
                name: "UX_Metars_Station_Time_Type",
                table: "Metars",
                columns: new[] { "StationIcao", "ObservationUtc", "ReportType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetarSkyConditions_MetarId",
                table: "MetarSkyConditions",
                column: "MetarId");

            migrationBuilder.CreateIndex(
                name: "IX_MetarWeatherPhenomena_MetarId",
                table: "MetarWeatherPhenomena",
                column: "MetarId");

            migrationBuilder.CreateIndex(
                name: "UX_Recipients_RecipientId",
                table: "Recipients",
                column: "RecipientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RecipientStates_RecipientId",
                table: "RecipientStates",
                column: "RecipientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TafChangePeriods_TafId",
                table: "TafChangePeriods",
                column: "TafId");

            migrationBuilder.CreateIndex(
                name: "IX_TafChangePeriodSkyConditions_TafChangePeriodId",
                table: "TafChangePeriodSkyConditions",
                column: "TafChangePeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_TafChangePeriodWeatherPhenomena_TafChangePeriodId",
                table: "TafChangePeriodWeatherPhenomena",
                column: "TafChangePeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_Tafs_StationIcao",
                table: "Tafs",
                column: "StationIcao");

            migrationBuilder.CreateIndex(
                name: "UX_Tafs_Station_Issuance_Type",
                table: "Tafs",
                columns: new[] { "StationIcao", "IssuanceUtc", "ReportType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GfsGrid");

            migrationBuilder.DropTable(
                name: "GfsModelRuns");

            migrationBuilder.DropTable(
                name: "GlobalSettings");

            migrationBuilder.DropTable(
                name: "MetarRunwayVisualRanges");

            migrationBuilder.DropTable(
                name: "MetarSkyConditions");

            migrationBuilder.DropTable(
                name: "MetarWeatherPhenomena");

            migrationBuilder.DropTable(
                name: "Recipients");

            migrationBuilder.DropTable(
                name: "RecipientStates");

            migrationBuilder.DropTable(
                name: "TafChangePeriodSkyConditions");

            migrationBuilder.DropTable(
                name: "TafChangePeriodWeatherPhenomena");

            migrationBuilder.DropTable(
                name: "WxStations");

            migrationBuilder.DropTable(
                name: "Metars");

            migrationBuilder.DropTable(
                name: "TafChangePeriods");

            migrationBuilder.DropTable(
                name: "Tafs");
        }
    }
}
