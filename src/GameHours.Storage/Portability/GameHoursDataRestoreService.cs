using System.Globalization;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Portability;

public sealed record GameHoursRestoreResult(
    string SourcePath,
    string SafetyBackupPath,
    DateTimeOffset RestoredAtUtc);

/// <summary>
/// Restores a complete GameHours SQLite backup. Callers must stop writers before invoking
/// this service. The selected backup is validated and migrated in staging before the live
/// database is replaced, and the current live database is backed up first for rollback.
/// </summary>
public sealed class GameHoursDataRestoreService
{
    private readonly GameHoursDatabase _database;

    public GameHoursDataRestoreService(GameHoursDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<GameHoursRestoreResult> RestoreBackupAsync(
        string sourcePath,
        string safetyBackupPath,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizePath(sourcePath, nameof(sourcePath));
        var safety = NormalizePath(safetyBackupPath, nameof(safetyBackupPath));
        var live = _database.DatabasePath;

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The selected GameHours backup does not exist.", source);
        }
        if (PathsEqual(source, live))
        {
            throw new ArgumentException("Restore source must be different from the live database.", nameof(sourcePath));
        }
        if (PathsEqual(safety, live) || PathsEqual(safety, source))
        {
            throw new ArgumentException("Safety backup must be different from both the live database and restore source.", nameof(safetyBackupPath));
        }

        var liveDirectory = Path.GetDirectoryName(live)
            ?? throw new InvalidOperationException("Could not determine the GameHours data directory.");
        Directory.CreateDirectory(liveDirectory);

        var rawStaging = Path.Combine(liveDirectory, $".restore-source-{Guid.NewGuid():N}.db");
        var readyStaging = Path.Combine(liveDirectory, $".restore-ready-{Guid.NewGuid():N}.db");
        var rollbackStaging = Path.Combine(liveDirectory, $".restore-rollback-{Guid.NewGuid():N}.db");
        var liveReplaced = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Snapshot the selected database through SQLite rather than copying its main file.
            // This also handles a source that was captured from a WAL-enabled database.
            await SnapshotDatabaseFileAsync(source, rawStaging, cancellationToken);

            // Migrate only the staging copy. A backup from a newer unsupported GameHours build
            // fails here before the current live database is touched.
            var stagedDatabase = new GameHoursDatabase(rawStaging);
            await stagedDatabase.InitializeAsync(cancellationToken);

            // Initialization uses WAL, so take a second SQLite snapshot after migration. The
            // resulting ready file is self-contained and safe to atomically replace into place.
            await SnapshotDatabaseAsync(stagedDatabase, readyStaging, cancellationToken);

            var portability = new GameHoursDataPortabilityService(_database);
            await portability.CreateBackupAsync(safety, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            SqliteConnection.ClearAllPools();
            DeleteSqliteSidecars(live);

            if (File.Exists(live))
            {
                File.Replace(readyStaging, live, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(readyStaging, live);
            }
            liveReplaced = true;

            // Re-open the exact path the application uses and verify it after replacement.
            await _database.InitializeAsync(cancellationToken);
            await using (var restored = _database.OpenConnection())
            {
                await EnsureIntegrityAsync(restored, cancellationToken);
            }

            return new GameHoursRestoreResult(source, safety, DateTimeOffset.UtcNow);
        }
        catch (Exception restoreException)
        {
            if (liveReplaced && File.Exists(safety))
            {
                try
                {
                    await SnapshotDatabaseFileAsync(safety, rollbackStaging, CancellationToken.None);
                    SqliteConnection.ClearAllPools();
                    DeleteSqliteSidecars(live);
                    if (File.Exists(live))
                    {
                        File.Replace(rollbackStaging, live, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(rollbackStaging, live);
                    }
                    await _database.InitializeAsync(CancellationToken.None);
                    await using var rolledBack = _database.OpenConnection();
                    await EnsureIntegrityAsync(rolledBack, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidDataException(
                        "GameHours restore failed and the automatic rollback also failed. The pre-restore safety backup was preserved.",
                        new AggregateException(restoreException, rollbackException));
                }
            }

            throw;
        }
        finally
        {
            DeleteIfExists(rawStaging);
            DeleteIfExists(readyStaging);
            DeleteIfExists(rollbackStaging);
            DeleteSqliteSidecars(rawStaging);
            DeleteSqliteSidecars(readyStaging);
            DeleteSqliteSidecars(rollbackStaging);
        }
    }

    private static async Task SnapshotDatabaseFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await source.OpenAsync(cancellationToken);
            await SnapshotConnectionAsync(source, destinationPath, cancellationToken);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("The selected file is not a valid readable GameHours SQLite backup.", exception);
        }
    }

    private static async Task SnapshotDatabaseAsync(
        GameHoursDatabase sourceDatabase,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = sourceDatabase.OpenConnection();
        await SnapshotConnectionAsync(source, destinationPath, cancellationToken);
    }

    private static async Task SnapshotConnectionAsync(
        SqliteConnection source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        DeleteIfExists(destinationPath);
        await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await target.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(target);
        await EnsureIntegrityAsync(target, cancellationToken);
        await target.CloseAsync();
    }

    private static async Task EnsureIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite integrity check failed: {result ?? "<no result>"}.");
        }
    }

    private static string NormalizePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", parameterName);
        }
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void DeleteSqliteSidecars(string databasePath)
    {
        DeleteIfExists(databasePath + "-wal");
        DeleteIfExists(databasePath + "-shm");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
