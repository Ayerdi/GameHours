using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class GameHoursDataRestoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-tests",
        Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestoreBackup_ReplacesLiveDatabase_AndPreservesSafetyBackup()
    {
        var livePath = Path.Combine(_directory, "live", "gamehours.db");
        var sourcePath = Path.Combine(_directory, "source", "gamehours.db");
        var safetyPath = Path.Combine(_directory, "backups", "pre-restore.db");

        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();
        await InsertGameAsync(live, Guid.NewGuid(), "Current live game");

        var source = new GameHoursDatabase(sourcePath);
        await source.InitializeAsync();
        var restoredGameId = Guid.NewGuid();
        await InsertGameAsync(source, restoredGameId, "Restored game");

        var service = new GameHoursDataRestoreService(live);
        var result = await service.RestoreBackupAsync(sourcePath, safetyPath);

        Assert.Equal(Path.GetFullPath(sourcePath), result.SourcePath);
        Assert.Equal(Path.GetFullPath(safetyPath), result.SafetyBackupPath);
        Assert.True(File.Exists(safetyPath));

        Assert.Equal(new[] { "Restored game" }, await ReadGameTitlesAsync(livePath));
        Assert.Equal(new[] { "Current live game" }, await ReadGameTitlesAsync(safetyPath));

        await using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = livePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await restored.OpenAsync();
        await using var integrity = restored.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", Convert.ToString(await integrity.ExecuteScalarAsync()));

        await using var game = restored.CreateCommand();
        game.CommandText = "SELECT COUNT(*) FROM games WHERE id = $id;";
        game.Parameters.AddWithValue("$id", restoredGameId.ToString("D"));
        Assert.Equal(1L, Convert.ToInt64(await game.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RestoreBackup_V4Source_IsMigratedToCurrentSchemaBeforeReplacingLiveDatabase()
    {
        var livePath = Path.Combine(_directory, "live-current", "gamehours.db");
        var sourcePath = Path.Combine(_directory, "source-v4", "gamehours.db");
        var safetyPath = Path.Combine(_directory, "backups", "pre-v4-restore.db");

        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();

        var source = new GameHoursDatabase(sourcePath);
        await source.InitializeAsync();
        var gameId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await InsertGameAsync(source, gameId, "V4 activity game");

        await using (var connection = source.OpenConnection())
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                INSERT INTO session_activity(
                    session_id, game_id, focused_duration_ms, active_duration_ms,
                    idle_threshold_ms, is_finalized, updated_at_utc, afk_filter_enabled)
                VALUES ($session_id, $game_id, 120000, 90000, 300000, 1, $updated_at_utc, 1);

                DROP INDEX idx_session_activity_game;
                ALTER TABLE session_activity RENAME TO session_activity_v5_test;
                CREATE TABLE session_activity (
                    session_id TEXT PRIMARY KEY,
                    game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                    focused_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (focused_duration_ms >= 0),
                    active_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (active_duration_ms >= 0 AND active_duration_ms <= focused_duration_ms),
                    idle_threshold_ms INTEGER NOT NULL CHECK (idle_threshold_ms > 0),
                    is_finalized INTEGER NOT NULL DEFAULT 0 CHECK (is_finalized IN (0, 1)),
                    updated_at_utc TEXT NOT NULL
                );
                INSERT INTO session_activity(
                    session_id, game_id, focused_duration_ms, active_duration_ms,
                    idle_threshold_ms, is_finalized, updated_at_utc)
                SELECT session_id, game_id, focused_duration_ms, active_duration_ms,
                       idle_threshold_ms, is_finalized, updated_at_utc
                FROM session_activity_v5_test;
                DROP TABLE session_activity_v5_test;
                CREATE INDEX idx_session_activity_game ON session_activity(game_id, updated_at_utc);
                DROP TABLE game_library_preferences;
                DROP TABLE game_external_identities;
                PRAGMA user_version = 4;
                UPDATE schema_info SET version = 4;
                """;
            downgrade.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
            downgrade.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
            downgrade.Parameters.AddWithValue("$updated_at_utc", DateTimeOffset.UtcNow.ToString("O"));
            await downgrade.ExecuteNonQueryAsync();
        }

        var service = new GameHoursDataRestoreService(live);
        await service.RestoreBackupAsync(sourcePath, safetyPath);

        await using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = livePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await restored.OpenAsync();

        await using var version = restored.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(8L, Convert.ToInt64(await version.ExecuteScalarAsync()));

        await using var coverageColumn = restored.CreateCommand();
        coverageColumn.CommandText = "SELECT COUNT(*) FROM pragma_table_info('achievement_observation_state') WHERE name = 'state_coverage';";
        Assert.Equal(1L, Convert.ToInt64(await coverageColumn.ExecuteScalarAsync()));

        await using var evidenceTable = restored.CreateCommand();
        evidenceTable.CommandText = "SELECT COUNT(*) FROM pragma_table_info('achievement_unlock_evidence');";
        Assert.Equal(11L, Convert.ToInt64(await evidenceTable.ExecuteScalarAsync()));

        await using var libraryPreferences = restored.CreateCommand();
        libraryPreferences.CommandText = "SELECT COUNT(*) FROM pragma_table_info('game_library_preferences');";
        Assert.Equal(5L, Convert.ToInt64(await libraryPreferences.ExecuteScalarAsync()));

        await using var externalIdentities = restored.CreateCommand();
        externalIdentities.CommandText = "SELECT COUNT(*) FROM pragma_table_info('game_external_identities');";
        Assert.Equal(4L, Convert.ToInt64(await externalIdentities.ExecuteScalarAsync()));

        await using var activity = restored.CreateCommand();
        activity.CommandText = """
            SELECT focused_duration_ms, active_duration_ms, idle_threshold_ms, afk_filter_enabled
            FROM session_activity
            WHERE session_id = $session_id;
            """;
        activity.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        await using var reader = await activity.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(120000L, reader.GetInt64(0));
        Assert.Equal(90000L, reader.GetInt64(1));
        Assert.Equal(300000L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
    }

    [Fact]
    public async Task RestoreBackup_InvalidSource_DoesNotTouchLiveDatabaseOrCreateSafetyBackup()
    {
        var livePath = Path.Combine(_directory, "live", "gamehours.db");
        var invalidPath = Path.Combine(_directory, "invalid.db");
        var safetyPath = Path.Combine(_directory, "backups", "pre-restore.db");

        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();
        await InsertGameAsync(live, Guid.NewGuid(), "Keep me");
        await File.WriteAllTextAsync(invalidPath, "not a sqlite database");

        var service = new GameHoursDataRestoreService(live);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreBackupAsync(invalidPath, safetyPath));

        Assert.False(File.Exists(safetyPath));
        Assert.Equal(new[] { "Keep me" }, await ReadGameTitlesAsync(livePath));
    }

    [Fact]
    public async Task RestoreBackup_RefusesLiveDatabaseAsSource()
    {
        var livePath = Path.Combine(_directory, "live", "gamehours.db");
        var safetyPath = Path.Combine(_directory, "backups", "pre-restore.db");
        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();

        var service = new GameHoursDataRestoreService(live);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RestoreBackupAsync(livePath, safetyPath));

        Assert.Contains("different from the live database", exception.Message, StringComparison.Ordinal);
    }

    private static async Task InsertGameAsync(GameHoursDatabase database, Guid gameId, string title)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
            VALUES ($id, $title, NULL, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", gameId.ToString("D"));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadGameTitlesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM games ORDER BY title COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync();
        var titles = new List<string>();
        while (await reader.ReadAsync())
        {
            titles.Add(reader.GetString(0));
        }
        return titles;
    }
}
