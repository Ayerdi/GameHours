using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gamehours-migrations", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LegacyDatabaseWithoutUserVersionMigratesToCurrentSchema()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "gamehours.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_info(version INTEGER NOT NULL); INSERT INTO schema_info(version) VALUES(1);";
            await command.ExecuteNonQueryAsync();
        }

        var database = new GameHoursDatabase(path);
        await database.InitializeAsync();

        await using var verify = database.OpenConnection();
        await using var versionCommand = verify.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(6L, Convert.ToInt64(await versionCommand.ExecuteScalarAsync()));
        await using var tableCommand = verify.CreateCommand();
        tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('game_candidates', 'session_activity');";
        Assert.Equal(2L, Convert.ToInt64(await tableCommand.ExecuteScalarAsync()));
        await using var coverageColumn = verify.CreateCommand();
        coverageColumn.CommandText = "SELECT COUNT(*) FROM pragma_table_info('achievement_observation_state') WHERE name = 'state_coverage';";
        Assert.Equal(1L, Convert.ToInt64(await coverageColumn.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task InitializeIsIdempotentAtCurrentVersion()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();
        await database.InitializeAsync();
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(6L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task CurrentSchemaRejectsActiveDurationWhenAfkEstimationIsDisabled()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        await using var connection = database.OpenConnection();
        var gameId = Guid.NewGuid().ToString("D");
        await using (var game = connection.CreateCommand())
        {
            game.CommandText = """
                INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
                VALUES($game_id, 'AFK constraint test', NULL,
                       '2026-08-23T10:00:00.0000000+00:00',
                       '2026-08-23T10:00:00.0000000+00:00');
                """;
            game.Parameters.AddWithValue("$game_id", gameId);
            await game.ExecuteNonQueryAsync();
        }

        await using var invalid = connection.CreateCommand();
        invalid.CommandText = """
            INSERT INTO session_activity(
                session_id, game_id, focused_duration_ms, active_duration_ms,
                idle_threshold_ms, is_finalized, updated_at_utc, afk_filter_enabled)
            VALUES($session_id, $game_id, 60000, 60000, 0, 1,
                   '2026-08-23T10:01:00.0000000+00:00', 0);
            """;
        invalid.Parameters.AddWithValue("$session_id", Guid.NewGuid().ToString("D"));
        invalid.Parameters.AddWithValue("$game_id", gameId);

        await Assert.ThrowsAsync<SqliteException>(() => invalid.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task VersionFourActivityRowsPreserveEnabledAfkMeaning()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "gamehours.db");
        var database = new GameHoursDatabase(path);
        await database.InitializeAsync();

        var gameId = Guid.NewGuid().ToString("D");
        var sessionId = Guid.NewGuid().ToString("D");
        await using (var connection = database.OpenConnection())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
                VALUES($game_id, 'Migration game', NULL, '2026-08-22T10:00:00.0000000+00:00', '2026-08-22T10:00:00.0000000+00:00');
                INSERT INTO session_activity(
                    session_id, game_id, focused_duration_ms, active_duration_ms,
                    idle_threshold_ms, is_finalized, updated_at_utc, afk_filter_enabled)
                VALUES($session_id, $game_id, 120000, 90000, 300000, 1,
                       '2026-08-22T10:02:00.0000000+00:00', 1);
                """;
            command.Parameters.AddWithValue("$game_id", gameId);
            command.Parameters.AddWithValue("$session_id", sessionId);
            await command.ExecuteNonQueryAsync();

            // Recreate the exact v4 activity shape, then advertise schema version 4.
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
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
                PRAGMA user_version = 4;
                UPDATE schema_info SET version = 4;
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        await using var verify = database.OpenConnection();
        await using var command2 = verify.CreateCommand();
        command2.CommandText = "SELECT idle_threshold_ms, afk_filter_enabled FROM session_activity WHERE session_id = $session_id;";
        command2.Parameters.AddWithValue("$session_id", sessionId);
        await using var reader = await command2.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(300000L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task VersionThreeDropsOldPendingSuggestionsButPreservesDecisions()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "gamehours.db");
        var database = new GameHoursDatabase(path);
        await database.InitializeAsync();

        await using (var connection = database.OpenConnection())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO game_candidates(
                    executable_path, executable_name, process_name, suggested_title,
                    confidence, method, role, evidence_json,
                    first_seen_at_utc, last_seen_at_utc, observation_count, status,
                    decision_role, resolved_at_utc)
                VALUES
                    ('C:\\Apps\\Browser\\browser.exe', 'browser.exe', 'browser', 'browser',
                     0.65, 'heuristic_graphics_candidate', 0, '[]',
                     '2026-08-22T10:00:00.0000000+00:00', '2026-08-22T10:00:00.0000000+00:00', 4, 0,
                     NULL, NULL),
                    ('C:\\Apps\\Ignored\\ignored.exe', 'ignored.exe', 'ignored', 'ignored',
                     0.10, 'unresolved', 8, '[]',
                     '2026-08-22T10:00:00.0000000+00:00', '2026-08-22T10:00:00.0000000+00:00', 1, 2,
                     8, '2026-08-22T10:01:00.0000000+00:00');
                PRAGMA user_version = 2;
                UPDATE schema_info SET version = 2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.InitializeAsync();

        await using var verify = database.OpenConnection();
        await using var pending = verify.CreateCommand();
        pending.CommandText = "SELECT COUNT(*) FROM game_candidates WHERE status = 0;";
        Assert.Equal(0L, Convert.ToInt64(await pending.ExecuteScalarAsync()));
        await using var decided = verify.CreateCommand();
        decided.CommandText = "SELECT COUNT(*) FROM game_candidates WHERE status = 2 AND decision_role = 8;";
        Assert.Equal(1L, Convert.ToInt64(await decided.ExecuteScalarAsync()));
        await using var activityTable = verify.CreateCommand();
        activityTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='session_activity';";
        Assert.Equal(1L, Convert.ToInt64(await activityTable.ExecuteScalarAsync()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
