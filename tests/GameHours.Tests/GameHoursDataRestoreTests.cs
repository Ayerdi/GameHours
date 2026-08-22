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
