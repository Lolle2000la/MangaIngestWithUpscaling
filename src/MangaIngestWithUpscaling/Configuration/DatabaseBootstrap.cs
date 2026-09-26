using System.Globalization;
using MangaIngestWithUpscaling.Data;
using MangaIngestWithUpscaling.Data.BackgroundTaskQueue;
using MangaIngestWithUpscaling.Services.ChapterMerging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MangaIngestWithUpscaling.Configuration;

/// <summary>
/// The database work that has to happen outside the normal request pipeline: pre-<c>Build</c> SQLite
/// file preparation, the PostgreSQL logs-table bootstrap, and the startup migration sequence
/// (including the .NET 10 upgrade backup and the advisory-locked PostgreSQL migration).
/// </summary>
public static class DatabaseBootstrap
{
    /// <summary>
    /// Prepares the SQLite database files before the host is built.
    /// <para>
    /// Sets WAL journal mode, which is idempotent and safe for existing databases. This runs before
    /// <c>builder.Build()</c> so <c>logs.db</c> is configured before Serilog opens it. For the logs
    /// database it also detects corruption and moves the file aside so a fresh one can be created
    /// instead of crashing the application on startup.
    /// </para>
    /// </summary>
    public static void PrepareSqliteDatabases(DatabaseConfiguration configuration)
    {
        if (!configuration.IsSqlite)
        {
            return;
        }

        var earlyDbPaths = new List<string>
        {
            configuration.SqliteDatabasePath!,
            configuration.LogsDbPath,
        };

        foreach (var earlyDbPath in earlyDbPaths)
        {
            var isLogsDb = earlyDbPath == configuration.LogsDbPath;
            try
            {
                using var earlyConn = new SqliteConnection($"Data Source={earlyDbPath}");
                earlyConn.Open();
                using var integrityCmd = earlyConn.CreateCommand();
                integrityCmd.CommandText = "PRAGMA integrity_check";
                var integrityResult = integrityCmd.ExecuteScalar() as string;
                if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    if (isLogsDb)
                    {
                        MoveCorruptDatabaseAside(earlyDbPath);
                        continue;
                    }

                    throw new IOException($"Database integrity check failed: {integrityResult}");
                }

                using var earlyCmd = earlyConn.CreateCommand();
                earlyCmd.CommandText = "PRAGMA journal_mode=WAL";
                earlyCmd.ExecuteScalar();
            }
            catch when (isLogsDb)
            {
                MoveCorruptDatabaseAside(earlyDbPath);
            }
            catch
            {
                // Non-critical: WAL mode will be re-verified with logging after migrations
            }
        }
    }

    /// <summary>
    /// Ensures the PostgreSQL <c>Logs</c> table exists before the Serilog sink starts writing. On
    /// PostgreSQL the logs live in the application database, and the sink does not create the table
    /// for us (<c>needAutoCreateTable</c> is disabled so the schema stays under our control).
    /// </summary>
    public static void EnsurePostgresLogsTable(DatabaseConfiguration configuration)
    {
        if (configuration.IsSqlite)
        {
            return;
        }

        // The DDL is IF NOT EXISTS, but concurrent DDL is not fully atomic: replicas starting
        // together can each observe a transient 23505/42P07/42710 (the other replica created the
        // object first) or 40P01 (deadlock), and an operator-configured lock_timeout can surface as
        // 55P03 during the same contention. Retry briefly so the loser converges instead of leaving
        // the sink (needAutoCreateTable: false) without a table to write to. Runs before the
        // advisory-locked migration deliberately, so the table exists the moment the sink starts.
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var logsConnection = new NpgsqlConnection(
                    configuration.PostgresConnectionString
                );
                logsConnection.Open();
                using var createLogsCommand = logsConnection.CreateCommand();
                createLogsCommand.CommandText = PostgresLogging.CreateTableSql;
                createLogsCommand.ExecuteNonQuery();
                break;
            }
            catch (PostgresException ex)
                when (IsRetryableLogsBootstrapFailure(ex) && attempt < maxAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
            catch (Exception ex)
            {
                // Best effort: this runs before the host (and its logger) exists, so stderr is the
                // only channel. A genuinely unusable database fails the migration below anyway; a
                // missing Logs table alone means the sink (needAutoCreateTable: false) drops writes,
                // so log the detail.
                Console.Error.WriteLine($"Failed to ensure the PostgreSQL Logs table exists: {ex}");
                break;
            }
        }
    }

    /// <summary>
    /// Applies the provider's migrations and the startup maintenance that follows them: the .NET 10
    /// upgrade backup/marker, WAL re-assertion, production vacuum, resetting stranded
    /// <c>Processing</c> tasks, and the backward-compatibility upgrade.
    /// </summary>
    public static async Task ApplyStartupMigrationsAsync(
        IServiceProvider scopedProvider,
        DatabaseConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger
    )
    {
        var dbContext = scopedProvider.GetRequiredService<ApplicationDbContext>();

        // The .NET 10 upgrade backup and marker only apply to the SQLite database file.
        string? upgradeMarkerFile = null;
        if (configuration.IsSqlite)
        {
            var dbPath = configuration.SqliteDatabasePath!;
            string dbDirectory =
                Path.GetDirectoryName(dbPath)
                ?? throw new InvalidOperationException("Unable to determine database directory");
            upgradeMarkerFile = Path.Combine(dbDirectory, ".net10-upgrade-complete");

            if (!File.Exists(upgradeMarkerFile) && File.Exists(dbPath))
            {
                try
                {
                    var backupPath = dbPath + ".bak";
                    File.Copy(dbPath, backupPath, overwrite: false);
                    logger.LogInformation(
                        "Created database backup at {BackupPath} before .NET 10 upgrade",
                        backupPath
                    );

                    // Also backup the logging database if it exists
                    var loggingDbPath = configuration.LogsDbPath;
                    if (File.Exists(loggingDbPath))
                    {
                        var loggingBackupPath = loggingDbPath + ".bak";
                        File.Copy(loggingDbPath, loggingBackupPath, overwrite: false);
                        logger.LogInformation(
                            "Created logging database backup at {BackupPath}",
                            loggingBackupPath
                        );
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to create database backup before .NET 10 upgrade. Continuing with migration..."
                    );
                }
            }
        }

        try
        {
            if (configuration.IsSqlite)
            {
                dbContext.Database.Migrate();
            }
            else
            {
                MigratePostgresWithAdvisoryLock(dbContext, logger);
            }

            logger.LogDebug("Database migrations applied successfully.");

            if (configuration.IsSqlite && upgradeMarkerFile is not null)
            {
                // Mark the .NET 10 upgrade as complete
                try
                {
                    await File.WriteAllTextAsync(
                        upgradeMarkerFile,
                        $"Upgrade completed on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"
                    );
                    logger.LogDebug("Marked .NET 10 upgrade as complete");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to create upgrade marker file, but migration completed successfully"
                    );
                }
            }

            // Re-assert and verify WAL mode for the main database (SQLite only).
            // SQLite returns the active journal mode, so check the returned value.
            if (configuration.IsSqlite)
            {
                try
                {
                    dbContext.Database.OpenConnection();
                    var mainConn = dbContext.Database.GetDbConnection();
                    using var walCmd = mainConn.CreateCommand();
                    walCmd.CommandText = "PRAGMA journal_mode=WAL";
                    var mode = (string?)await walCmd.ExecuteScalarAsync() ?? "unknown";
                    if (mode == "wal")
                        logger.LogDebug("WAL journal mode enabled for main database.");
                    else
                        logger.LogWarning(
                            "WAL journal mode could not be enabled for the main database (current mode: {Mode}). "
                                + "The database may be more susceptible to corruption on unexpected shutdowns.",
                            mode
                        );
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to enable WAL journal mode for the main database. "
                            + "The database may be more susceptible to corruption on unexpected shutdowns."
                    );
                }
                finally
                {
                    dbContext.Database.CloseConnection();
                }
            }

            // Best-effort: re-apply WAL mode for the logging database. Only relevant when logs are
            // stored in a local SQLite file.
            if (configuration.IsSqlite)
            {
                try
                {
                    var loggingDbContext = scopedProvider.GetRequiredService<LoggingDbContext>();
                    loggingDbContext.Database.OpenConnection();
                    try
                    {
                        var loggingConn = loggingDbContext.Database.GetDbConnection();
                        using var walCmd = loggingConn.CreateCommand();
                        walCmd.CommandText = "PRAGMA journal_mode=WAL";
                        var mode = (string?)await walCmd.ExecuteScalarAsync() ?? "unknown";
                        if (mode == "wal")
                            logger.LogDebug("WAL journal mode enabled for logging database.");
                        else
                            logger.LogWarning(
                                "WAL journal mode could not be enabled for the logging database (current mode: {Mode}).",
                                mode
                            );
                    }
                    finally
                    {
                        loggingDbContext.Database.CloseConnection();
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to enable WAL journal mode for logging database."
                    );
                }
            }

            if (environment.IsProduction())
            {
                if (configuration.IsSqlite)
                {
                    // A quick check to see if vacuum is needed could go here (e.g. checking file size)
                    await dbContext.Database.ExecuteSqlRawAsync("VACUUM;");
                    logger.LogInformation("Database vacuumed successfully.");
                }

                // Also vacuum the logging database to reclaim space from truncated logs (SQLite only).
                if (configuration.IsSqlite)
                {
                    try
                    {
                        var loggingDbContext =
                            scopedProvider.GetRequiredService<LoggingDbContext>();
                        await loggingDbContext.Database.ExecuteSqlRawAsync("VACUUM;");
                        logger.LogInformation("Logging database vacuumed successfully.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to vacuum logging database.");
                    }
                }
            }

            // reset any tasks that were "Processing" (e.g. during a crash) back to "Pending"
            await dbContext
                .PersistedTasks.Where(task => task.Status == PersistedTaskStatus.Processing)
                .ExecuteUpdateAsync(s =>
                    s.SetProperty(p => p.Status, p => PersistedTaskStatus.Pending)
                );

            // Validate and upgrade existing merged chapter records for backward compatibility
            try
            {
                var backwardCompatibilityService =
                    scopedProvider.GetRequiredService<IBackwardCompatibilityService>();
                await backwardCompatibilityService.ValidateAndUpgradeExistingRecordsAsync();
                logger.LogDebug("Backward compatibility validation completed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Backward compatibility validation failed, but application will continue. Some merge functionality may be affected for existing records."
                );
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"An error occurred while applying migrations: {ex.Message}");

            if (!configuration.IsSqlite)
            {
                // An external database that failed to migrate means every later query runs against
                // an unknown schema. Fail fast so the orchestrator restarts the app once the
                // database is reachable; the previous tolerant behavior is kept for the local SQLite
                // file.
                throw;
            }
        }
    }

    /// <summary>
    /// Applies the PostgreSQL migrations under a session-level advisory lock. Several replicas of
    /// the app may start at the same time against the same database; without serialization they
    /// race on the shared <c>__EFMigrationsHistory</c> table (duplicate inserts, "relation already
    /// exists", deadlocks) and, because a failed PostgreSQL migration now fails fast, the losers
    /// would crash-loop. The lock is held on a dedicated connection so it survives independently of
    /// EF's own connection handling for the whole migration, and it is released (or auto-released
    /// when the connection closes) in a finally so it cannot mask a migration error.
    /// </summary>
    private static void MigratePostgresWithAdvisoryLock(
        ApplicationDbContext dbContext,
        ILogger logger
    )
    {
        // Fixed, application-specific advisory-lock key ("MangaIU" plus a version byte). Both the
        // lock and the unlock must use the same value; it must not change between releases or
        // replicas of different versions would stop serializing with each other.
        const long migrationLockKey = 0x4D616E6761495500L;

        // Disable pooling so disposing the connection definitely closes the session and releases the
        // session-level advisory lock, even if the explicit unlock below fails.
        var lockConnectionString = new NpgsqlConnectionStringBuilder(
            dbContext.Database.GetConnectionString()
        )
        {
            Pooling = false,
        }.ConnectionString;

        using var lockConnection = new NpgsqlConnection(lockConnectionString);
        lockConnection.Open();

        using (var lockCommand = lockConnection.CreateCommand())
        {
            lockCommand.CommandText = "SELECT pg_advisory_lock(@key)";
            lockCommand.Parameters.AddWithValue("key", migrationLockKey);
            lockCommand.ExecuteNonQuery();
        }

        try
        {
            dbContext.Database.Migrate();
        }
        finally
        {
            try
            {
                using var unlockCommand = lockConnection.CreateCommand();
                unlockCommand.CommandText = "SELECT pg_advisory_unlock(@key)";
                unlockCommand.Parameters.AddWithValue("key", migrationLockKey);
                unlockCommand.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                // Never let a failed unlock hide the real migration result.
                logger.LogDebug(ex, "Failed to release the PostgreSQL migration advisory lock");
            }
        }
    }

    private static void MoveCorruptDatabaseAside(string dbPath)
    {
        try
        {
            if (File.Exists(dbPath))
            {
                var timestamp = DateTime.UtcNow.ToString(
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture
                );
                var backupPath = $"{dbPath}.corrupted.{timestamp}";
                File.Move(dbPath, backupPath);
            }

            var walPath = dbPath + "-wal";
            var shmPath = dbPath + "-shm";
            try
            {
                if (File.Exists(walPath))
                    File.Delete(walPath);
            }
            catch { }

            try
            {
                if (File.Exists(shmPath))
                    File.Delete(shmPath);
            }
            catch { }
        }
        catch
        {
            // Best effort: if we can't move/delete the files, the startup will
            // proceed normally and may fail with the original error
        }
    }

    // Transient failures a concurrent replica's CREATE TABLE/INDEX can cause even with IF NOT EXISTS:
    // the object is created by another session between our catalog check and our DDL.
    private static bool IsRetryableLogsBootstrapFailure(PostgresException ex) =>
        ex.SqlState
            is PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.DuplicateTable
                or PostgresErrorCodes.DuplicateObject
                or PostgresErrorCodes.DeadlockDetected
                or PostgresErrorCodes.LockNotAvailable;
}
