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
        Assert.Equal(2L, Convert.ToInt64(await versionCommand.ExecuteScalarAsync()));
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
        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
