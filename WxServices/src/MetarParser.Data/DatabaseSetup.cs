using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Consolidates all database schema creation and migration logic into a
/// single idempotent method.  Every service calls <see cref="EnsureSchemaAsync"/>
/// at startup; the first one to run creates the database and all tables,
/// and subsequent calls are harmless no-ops.
/// <para>
/// WX-28 wraps the schema setup in a retry loop so that a service booting
/// before SQL Server is ready (e.g. after a Windows-Update-driven reboot)
/// waits for the server to come up instead of crashing on the first
/// <c>SqlException error 26</c>.  Transient failures log at <c>WARN</c>; a
/// <see cref="DatabaseUnavailableException"/> is thrown only after every
/// attempt in <see cref="DatabaseStartupRetryOptions"/> has failed.
/// </para>
/// </summary>
public static class DatabaseSetup
{
    /// <summary>
    /// Ensures the <c>WeatherData</c> database and all required tables exist,
    /// retrying transient connection failures according to
    /// <paramref name="retry"/>.
    /// <para>
    /// <see cref="WeatherDataContext.Database.EnsureCreatedAsync"/> creates
    /// the core tables defined in <see cref="WeatherDataContext.OnModelCreating"/>
    /// only when the database is entirely absent — so this method also covers
    /// the first-run case where SQL Server is up but the <c>WeatherData</c>
    /// database itself does not yet exist.  The idempotent DDL statements
    /// below handle tables and columns that were added after the initial
    /// schema.
    /// </para>
    /// </summary>
    /// <param name="dbOptions">EF Core options for the <see cref="WeatherDataContext"/>.</param>
    /// <param name="retry">Retry schedule; <c>null</c> uses <see cref="DatabaseStartupRetryOptions.Default"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="DatabaseUnavailableException">
    /// Thrown after every attempt has failed with a transient connection
    /// error.  Non-transient errors (e.g. permission problems, schema
    /// conflicts) propagate immediately without retry.
    /// </exception>
    public static async Task EnsureSchemaAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        DatabaseStartupRetryOptions? retry = null,
        CancellationToken ct = default)
    {
        retry ??= DatabaseStartupRetryOptions.Default;
        var maxAttempts = Math.Max(1, retry.MaxAttempts);

        Exception? lastTransient = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await EnsureSchemaCoreAsync(dbOptions, ct);
                if (attempt > 1)
                {
                    Logger.Info(
                        $"Database became available on attempt {attempt}/{maxAttempts}.");
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientConnectionError(ex))
            {
                lastTransient = ex;

                if (attempt >= maxAttempts) break;

                var delay = retry.DelayAfterAttempt(attempt);
                Logger.Warn(
                    $"Database not ready (attempt {attempt}/{maxAttempts}): " +
                    $"{ex.GetBaseException().Message.TrimEnd('.')} — retrying in {delay.TotalSeconds:F0}s.");

                await Task.Delay(delay, ct);
            }
        }

        throw new DatabaseUnavailableException(
            $"Database did not become available after {maxAttempts} attempt(s). " +
            "SQL Server may be down, unreachable, or misconfigured.",
            lastTransient);
    }

    private static async Task EnsureSchemaCoreAsync(
        DbContextOptions<WeatherDataContext> dbOptions,
        CancellationToken ct)
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

        // WX-13: Country and State/Province/Region fields on WxStations.
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'Region')
                ALTER TABLE [WxStations] ADD [Region] nvarchar(100) NULL;", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'RegionCode')
                ALTER TABLE [WxStations] ADD [RegionCode] nvarchar(10) NULL;", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'RegionAbbr')
                ALTER TABLE [WxStations] ADD [RegionAbbr] nvarchar(10) NULL;", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'Country')
                ALTER TABLE [WxStations] ADD [Country] nvarchar(100) NULL;", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'CountryCode')
                ALTER TABLE [WxStations] ADD [CountryCode] nchar(2) NULL;", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'WxStations') AND name = N'CountryAbbr')
                ALTER TABLE [WxStations] ADD [CountryAbbr] nvarchar(10) NULL;", ct);

        // WX-22: ReceivedUtc on Metars and Tafs.  Column was introduced in
        // commit a70d81f (2026-03-30) as a NOT NULL EF property populated by
        // the mappers, but no idempotent migration shipped with it.  These
        // guards let a fresh clone or restore-from-old-backup pick up the
        // column safely.  The DEFAULT SYSUTCDATETIME() only applies to the
        // backfill of pre-existing rows at ALTER time — EF always supplies a
        // value on insert, so the constraint is a no-op for new rows.
        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'Metars') AND name = N'ReceivedUtc')
                ALTER TABLE [Metars] ADD [ReceivedUtc] datetime2 NOT NULL
                    CONSTRAINT [DF_Metars_ReceivedUtc] DEFAULT SYSUTCDATETIME();", ct);

        await db.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT 1 FROM sys.columns
                           WHERE object_id = OBJECT_ID(N'Tafs') AND name = N'ReceivedUtc')
                ALTER TABLE [Tafs] ADD [ReceivedUtc] datetime2 NOT NULL
                    CONSTRAINT [DF_Tafs_ReceivedUtc] DEFAULT SYSUTCDATETIME();", ct);
    }

    /// <summary>
    /// Classifies an exception raised from EF Core startup as transient
    /// (retry) vs. permanent (fail fast).  Transient cases cover the
    /// post-reboot race where SQL Server has not yet opened its network
    /// listener, has not yet attached the database, or is throttling
    /// connections while the instance warms up.  Permanent cases — invalid
    /// configuration, schema conflicts, permission errors — fall through
    /// unchanged so the existing <c>Logger.Error("Fatal error during
    /// startup.")</c> block continues to surface real bugs immediately.
    /// </summary>
    private static bool IsTransientConnectionError(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is SqlException sql && IsTransientSqlNumber(sql.Number))
                return true;
        }
        return false;
    }

    /// <summary>
    /// SQL Server / SqlClient error numbers that indicate a transient
    /// connection-level condition — either "server not reachable yet" or
    /// "server is throttling under load."  Deliberately conservative: login
    /// failures (18456) and permissions errors (e.g. 229) are NOT included,
    /// because those are configuration bugs that should fail fast rather
    /// than spin for five minutes.
    /// </summary>
    private static bool IsTransientSqlNumber(int number) => number switch
    {
        // Client-side timeouts and network-layer failures.
        -2     => true,   // Connection timeout expired.
        20     => true,   // The instance of SQL Server you attempted to connect to does not support encryption (often transient during startup).
        26     => true,   // Error Locating Server/Instance Specified — the post-reboot case we're fixing.
        40     => true,   // Could not open a connection to SQL Server.
        53     => true,   // Network path not found.
        64     => true,   // Specified network name is no longer available.
        121    => true,   // Semaphore timeout period has expired.
        233    => true,   // No process is on the other end of the pipe.
        258    => true,   // Cannot wait on a mutex.
        1205   => true,   // Deadlock victim.
        1222   => true,   // Lock request time out.
        10053  => true,   // A transport-level error has occurred (connection forcibly closed).
        10054  => true,   // Connection reset by peer.
        10060  => true,   // Connection attempt timed out.
        10061  => true,   // No connection because target machine actively refused it.
        11001  => true,   // Host not found (DNS still warming up, for named-instance lookups).

        _ => false,
    };
}
