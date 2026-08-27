using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class GameHoursRestoreIdentityTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gamehours-restore-identity", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Restore_UnmarkedDatabaseCannotImpersonateLegacyGameHoursWithSchemaMarkerAlone()
    {
        var livePath = Path.Combine(_directory, "live.db");
        var sourcePath = Path.Combine(_directory, "impostor.db");
        var safetyPath = Path.Combine(_directory, "safety.db");
        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();

        await using (var source = new SqliteConnection($"Data Source={sourcePath}"))
        {
            await source.OpenAsync();
            await using var command = source.CreateCommand();
            command.CommandText = "CREATE TABLE schema_info(version INTEGER NOT NULL); INSERT INTO schema_info(version) VALUES(1);";
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GameHoursDataRestoreService(live).RestoreBackupAsync(sourcePath, safetyPath));

        Assert.Contains("missing required table", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(safetyPath));

        await using var connection = live.OpenConnection();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM games;";
        Assert.Equal(0L, Convert.ToInt64(await query.ExecuteScalarAsync()));
    }
}
