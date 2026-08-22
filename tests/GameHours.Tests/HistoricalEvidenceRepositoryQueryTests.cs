using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class HistoricalEvidenceRepositoryQueryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gamehours-historical-evidence", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BulkQueriesExecuteAgainstInitializedDatabase()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var trackingState = new SqliteTrackingStateRepository(database);
        var sessions = new SqliteSessionRepository(database);
        var repository = new SqliteHistoricalEvidenceRepository(database, trackingState, sessions);

        var all = await repository.GetAllAsync();
        var forGame = await repository.GetForGameAsync(Guid.NewGuid());

        Assert.Empty(all);
        Assert.Empty(forGame);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
