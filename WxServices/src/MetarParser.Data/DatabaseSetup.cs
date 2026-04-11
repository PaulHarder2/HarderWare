using Microsoft.EntityFrameworkCore;

namespace MetarParser.Data;

/// <summary>
/// Consolidates all database schema creation and migration logic into a
/// single idempotent method.  Every service calls <see cref="EnsureSchemaAsync"/>
/// at startup; the first one to run creates the database and all tables,
/// and subsequent calls are harmless no-ops.
/// </summary>
public static class DatabaseSetup
{
    /// <summary>
    /// Ensures the <c>WeatherData</c> database and all required tables exist.
    /// <para>
    /// <see cref="WeatherDataContext.Database.EnsureCreatedAsync"/> creates the
    /// core tables defined in <see cref="WeatherDataContext.OnModelCreating"/>
    /// only when the database is entirely absent.  The explicit DDL statements
    /// below handle tables and columns that were added after the initial schema,
    /// using <c>IF NOT EXISTS</c> guards so every statement is idempotent.
    /// </para>
    /// </summary>
    /// <param name="dbOptions">EF Core options for the <see cref="WeatherDataContext"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task EnsureSchemaAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        CancellationToken ct = default)
    {
        await using var db = new WeatherDataContext(dbOptions);

        await db.Database.EnsureCreatedAsync(ct);

        // ── Tables added after initial schema ────────────────────────────────

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GfsModelRuns')
            BEGIN
                CREATE TABLE [GfsModelRuns] (
                    [ModelRunUtc] datetime2 NOT NULL,
                    [IsComplete]  bit       NOT NULL DEFAULT 0,
                    CONSTRAINT [PK_GfsModelRuns] PRIMARY KEY ([ModelRunUtc])
                );
            END", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GfsGrid')
            BEGIN
                CREATE TABLE [GfsGrid] (
                    [Id]           int       NOT NULL IDENTITY,
                    [ModelRunUtc]  datetime2 NOT NULL,
                    [ForecastHour] int       NOT NULL,
                    [Lat]          real      NOT NULL,
                    [Lon]          real      NOT NULL,
                    [TmpC]         real      NULL,
                    [DwpC]         real      NULL,
                    [UGrdMs]       real      NULL,
                    [VGrdMs]       real      NULL,
                    [PRateKgM2s]   real      NULL,
                    [TcdcPct]      real      NULL,
                    [CapeJKg]      real      NULL,
                    CONSTRAINT [PK_GfsGrid] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [UX_GfsGrid_Run_Hour_LatLon]
                    ON [GfsGrid] ([ModelRunUtc], [ForecastHour], [Lat], [Lon]);
                CREATE INDEX [IX_GfsGrid_Run_Hour]
                    ON [GfsGrid] ([ModelRunUtc], [ForecastHour]);
            END", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Recipients')
            BEGIN
                CREATE TABLE [Recipients] (
                    [Id]                 int            NOT NULL IDENTITY,
                    [RecipientId]        nvarchar(100)  NOT NULL,
                    [Email]              nvarchar(200)  NOT NULL,
                    [Name]               nvarchar(200)  NOT NULL,
                    [Language]           nvarchar(50)   NULL,
                    [Timezone]           nvarchar(100)  NOT NULL DEFAULT 'UTC',
                    [ScheduledSendHours] nvarchar(50)   NULL,
                    [Address]            nvarchar(500)  NULL,
                    [LocalityName]       nvarchar(200)  NULL,
                    [Latitude]           float          NULL,
                    [Longitude]          float          NULL,
                    [MetarIcao]          nvarchar(100)  NULL,
                    [TafIcao]            nvarchar(10)   NULL,
                    [TempUnit]           nvarchar(10)   NOT NULL DEFAULT 'F',
                    [PressureUnit]       nvarchar(10)   NOT NULL DEFAULT 'inHg',
                    [WindSpeedUnit]      nvarchar(10)   NOT NULL DEFAULT 'mph',
                    CONSTRAINT [PK_Recipients] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [UX_Recipients_RecipientId]
                    ON [Recipients] ([RecipientId]);
            END", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'GlobalSettings')
            BEGIN
                CREATE TABLE [GlobalSettings] (
                    [Id]              int            NOT NULL,
                    [ClaudeApiKey]    nvarchar(500)  NULL,
                    [SmtpUsername]    nvarchar(200)  NULL,
                    [SmtpPassword]    nvarchar(200)  NULL,
                    [SmtpFromAddress] nvarchar(200)  NULL,
                    CONSTRAINT [PK_GlobalSettings] PRIMARY KEY ([Id])
                );
                INSERT INTO [GlobalSettings] ([Id]) VALUES (1);
            END", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'RecipientStates')
            BEGIN
                CREATE TABLE [RecipientStates] (
                    [Id]                      int           NOT NULL IDENTITY,
                    [RecipientId]             nvarchar(100) NOT NULL,
                    [LastScheduledSentUtc]    datetime2     NULL,
                    [LastUnscheduledSentUtc]  datetime2     NULL,
                    [LastSnapshotFingerprint] nvarchar(200) NULL,
                    [LastMetarIcao]           nchar(4)      NULL,
                    CONSTRAINT [PK_RecipientStates] PRIMARY KEY ([Id])
                );
                CREATE UNIQUE INDEX [UX_RecipientStates_RecipientId]
                    ON [RecipientStates] ([RecipientId]);
            END", ct);

        // ── Column migrations ────────────────────────────────────────────────

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID('RecipientStates') AND name = 'LastMetarIcao')
                ALTER TABLE [RecipientStates] ADD [LastMetarIcao] nchar(4) NULL;", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'Municipality'
            )
            ALTER TABLE [WxStations] ADD [Municipality] nvarchar(100) NULL;", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'AlwaysFetchDirect'
            )
            ALTER TABLE [WxStations] ADD [AlwaysFetchDirect] bit NULL;", ct);
    }
}
