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
        Assert.Equal(3L, Convert.ToInt64(await versionCommand.ExecuteScalarAsync()));
        await using var tableCommand = verify.CreateCommand();
        tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='game_candidates';";
        Assert.Equal(1L, Convert.ToInt64(await tableCommand.ExecuteScalarAsync()));
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
        Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
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
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
