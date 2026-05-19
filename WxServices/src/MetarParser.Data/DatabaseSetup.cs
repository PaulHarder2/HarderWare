using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Consolidates all database schema creation and upgrade logic into a single
/// idempotent method.  Every service calls <see cref="EnsureSchemaAsync"/> at
/// startup; the first one to run applies any pending EF Core migrations, and
/// subsequent calls are no-ops.
/// <para>
/// WX-28 wraps the schema setup in a retry loop so that a service booting
/// before SQL Server is ready (e.g. after a Windows-Update-driven reboot)
/// waits for the server to come up instead of crashing on the first
/// <c>SqlException error 26</c>.  Transient failures log at <c>WARN</c>; a
/// <see cref="DatabaseUnavailableException"/> is thrown only after every
/// attempt in <see cref="DatabaseStartupRetryOptions"/> has failed.
/// </para>
/// <para>
/// WX-72 replaced the previous <c>EnsureCreatedAsync</c> + hand-written
/// idempotent DDL pattern with EF Core Migrations.  Existing installations
/// whose schema was created by the older pattern are silently baselined on
/// first run with the new code: if the schema appears to exist but
/// <c>__EFMigrationsHistory</c> does not, the baseline migration is marked
/// as already-applied so <see cref="DatabaseFacade.MigrateAsync"/> does not
/// try to re-create tables that are already present.
/// </para>
/// </summary>
public static class DatabaseSetup
{
    /// <summary>
    /// Ensures the <c>WeatherData</c> database and all required tables exist,
    /// retrying transient connection failures according to
    /// <paramref name="retry"/>.
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

        if (!await db.Database.CanConnectAsync(ct))
        {
            // The database doesn't exist yet — MigrateAsync will create it
            // from scratch.  The CREATE DATABASE step cannot be protected by
            // sp_getapplock (the applock requires an existing-DB connection),
            // so concurrent first-boot races on CREATE DATABASE are handled
            // by classifying error 1801 ("database already exists") as
            // transient in IsTransientSqlNumber and letting the WX-28 retry
            // loop converge.
            await db.Database.MigrateAsync(ct);
            return;
        }

        // The database exists, so multiple services racing to apply pending
        // migrations would corrupt the schema on the loser side.  Serialize
        // the migration work across services with sp_getapplock — SQL Server's
        // built-in named-lock primitive.  LockOwner='Session' ties the lock's
        // lifetime to this SQL connection, so a crashed service auto-releases
        // it when SQL Server detects the dead connection.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await AcquireMigrationsApplockAsync(db, ct);
            try
            {
                await BaselineExistingSchemaIfNeededAsync(db, ct);
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                await ReleaseMigrationsApplockAsync(db, ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Resource name for the SQL Server applock that serialises migration
    /// runs across services.  Any value works as long as every service
    /// agrees; this one is unambiguous in shared logs and `sp_getapplock`
    /// diagnostics.
    /// </summary>
    private const string MigrationsApplockResource = "WxServices.MigrateAsync";

    /// <summary>
    /// Maximum time a service will wait for another service to release the
    /// migrations applock before giving up.  Two minutes is generous: a
    /// typical migration runs in seconds.  The cap protects against the
    /// pathological case where the lock holder hangs without crashing
    /// (which would otherwise release the Session-scoped lock).
    /// </summary>
    private const int MigrationsApplockTimeoutMs = 120_000;

    private static async Task AcquireMigrationsApplockAsync(
        WeatherDataContext db, CancellationToken ct)
    {
        // sp_getapplock returns negative values on failure (timeout, deadlock,
        // canceled, error).  Raise those as a thrown exception so the WX-28
        // retry loop classifies the failure correctly instead of silently
        // proceeding with an unheld lock.
        var rows = await db.Database.SqlQuery<int>(
            $@"DECLARE @r int;
               EXEC @r = sp_getapplock
                   @Resource = {MigrationsApplockResource},
                   @LockMode = N'Exclusive',
                   @LockOwner = N'Session',
                   @LockTimeout = {MigrationsApplockTimeoutMs};
               SELECT @r AS Value;").ToListAsync(ct);

        var result = rows.Count > 0 ? rows[0] : int.MinValue;
        if (result < 0)
        {
            throw new MigrationApplockException(
                result, MigrationsApplockResource, MigrationsApplockTimeoutMs);
        }
    }

    /// <summary>
    /// Raised when <c>sp_getapplock</c> returns a negative status code: <c>-1</c>
    /// timeout, <c>-2</c> canceled, <c>-3</c> deadlock victim, or <c>-999</c>
    /// parameter validation error.  The wait failures (-1, -2, -3) are
    /// classified as transient by <see cref="IsTransientConnectionError"/> so
    /// the WX-28 retry loop catches and retries them; <c>-999</c> is a code
    /// bug in our <c>EXEC sp_getapplock</c> call and is *not* classified as
    /// transient so it fails fast.
    /// </summary>
    private sealed class MigrationApplockException : Exception
    {
        public MigrationApplockException(int resultCode, string resource, int timeoutMs)
            : base(BuildMessage(resultCode, resource, timeoutMs))
        {
            ResultCode = resultCode;
        }

        public int ResultCode { get; }

        private static string BuildMessage(int resultCode, string resource, int timeoutMs)
        {
            var reason = resultCode switch
            {
                -1 => $"timed out after {timeoutMs / 1000}s waiting for another service to release the lock",
                -2 => "request was canceled",
                -3 => "request was chosen as a deadlock victim",
                -999 => "sp_getapplock parameter validation failed (likely a code bug)",
                _ => $"sp_getapplock returned unexpected code {resultCode}",
            };
            return $"Failed to acquire migrations applock '{resource}': {reason}.";
        }
    }

    private static async Task ReleaseMigrationsApplockAsync(
        WeatherDataContext db, CancellationToken ct)
    {
        // Best-effort release.  SQL Server releases Session-scoped applocks
        // automatically when the connection closes, so a failure here is
        // harmless — log it and move on rather than masking the real
        // exception that's already unwinding the stack.
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"EXEC sp_releaseapplock
                    @Resource = {MigrationsApplockResource},
                    @LockOwner = N'Session';", ct);
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"Releasing migrations applock failed: {ex.Message.TrimEnd('.')}.  " +
                "The lock will release on connection close regardless.");
        }
    }

    /// <summary>
    /// Detects the case where a database created by the pre-WX-72 schema setup
    /// (i.e. <c>EnsureCreatedAsync</c> + hand-written idempotent DDL) is being
    /// upgraded to the migrations-based setup for the first time.  In that
    /// case the schema is already present but <c>__EFMigrationsHistory</c>
    /// is not, so a plain <see cref="DatabaseFacade.MigrateAsync"/> would
    /// attempt to re-create tables that already exist and fail.  The fix is
    /// to insert a row for the baseline migration into a freshly-created
    /// history table, making EF treat the baseline as already-applied.
    /// </summary>
    private static async Task BaselineExistingSchemaIfNeededAsync(
        WeatherDataContext db, CancellationToken ct)
    {
        // If the database itself doesn't exist yet, MigrateAsync will create
        // it from scratch — no baselining needed.
        if (!await db.Database.CanConnectAsync(ct)) return;

        // If __EFMigrationsHistory is already present, EF can reason about
        // state on its own; do nothing.
        if (await SchemaObjectExistsAsync(db, "__EFMigrationsHistory", ct)) return;

        // If GfsGrid (a table introduced in the pre-migrations world and
        // present in the baseline) does not exist, the database is either
        // brand-new or pre-dates that table.  Either way, MigrateAsync can
        // safely run from the beginning.
        if (!await SchemaObjectExistsAsync(db, "GfsGrid", ct)) return;

        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        var baselineMigrationId = migrationsAssembly.Migrations.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .First();

        Logger.Info(
            $"Baselining existing database: marking '{baselineMigrationId}' " +
            "as already applied.");

        // All four services call EnsureSchemaAsync at Windows boot; on the
        // first start after the WX-72 upgrade they can race here.  TRY/CATCH
        // around the CREATE TABLE and the INSERT keeps the operation
        // idempotent: the loser of the race silently no-ops on the specific
        // SQL Server error numbers ("object already exists" for CREATE,
        // "duplicate key" for INSERT).  Any other error still propagates.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                BEGIN TRY
                    CREATE TABLE [__EFMigrationsHistory] (
                        [MigrationId] nvarchar(150) NOT NULL,
                        [ProductVersion] nvarchar(32) NOT NULL,
                        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
                    );
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() <> 2714 THROW;
                END CATCH;

                BEGIN TRY
                    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
                    VALUES ({baselineMigrationId}, {EfCoreProductVersion});
                END TRY
                BEGIN CATCH
                    IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
                END CATCH;", ct);

            await tx.CommitAsync(ct);
        });
    }

    private static async Task<bool> SchemaObjectExistsAsync(
        WeatherDataContext db, string tableName, CancellationToken ct)
    {
        var result = await db.Database
            .SqlQuery<int?>($"SELECT OBJECT_ID({tableName}, 'U') AS Value")
            .ToListAsync(ct);
        return result.Count > 0 && result[0].HasValue;
    }

    /// <summary>
    /// EF Core product version recorded into <c>__EFMigrationsHistory</c> for
    /// baselined rows.  Must match the <c>Microsoft.EntityFrameworkCore.SqlServer</c>
    /// package version in <c>MetarParser.Data.csproj</c>.  Used only for the
    /// baselining backfill — migrations generated by EF tooling fill this
    /// automatically.
    /// </summary>
    private const string EfCoreProductVersion = "8.0.0";

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

            // Applock wait failures — another service is holding the migration
            // lock; the retry loop should converge once that service finishes.
            // Parameter-validation (-999) is a code bug and intentionally stays
            // non-transient.
            if (cur is MigrationApplockException { ResultCode: -1 or -2 or -3 })
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
        -2 => true,   // Connection timeout expired.
        20 => true,   // The instance of SQL Server you attempted to connect to does not support encryption (often transient during startup).
        26 => true,   // Error Locating Server/Instance Specified — the post-reboot case we're fixing.
        40 => true,   // Could not open a connection to SQL Server.
        53 => true,   // Network path not found.
        64 => true,   // Specified network name is no longer available.
        121 => true,   // Semaphore timeout period has expired.
        233 => true,   // No process is on the other end of the pipe.
        258 => true,   // Cannot wait on a mutex.
        1205 => true,   // Deadlock victim.
        1222 => true,   // Lock request time out.
        1801 => true,   // CREATE DATABASE failed: database '<name>' already exists.  Fresh-install race between services; retry sees the existing DB and proceeds normally.
        10053 => true,   // A transport-level error has occurred (connection forcibly closed).
        10054 => true,   // Connection reset by peer.
        10060 => true,   // Connection attempt timed out.
        10061 => true,   // No connection because target machine actively refused it.
        11001 => true,   // Host not found (DNS still warming up, for named-instance lookups).

        _ => false,
    };
}