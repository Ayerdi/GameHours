using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class AchievementObservationBaselineRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-achievement-baseline-tests",
        Guid.NewGuid().ToString("N"));

    private GameHoursDatabase Database => new(Path.Combine(_directory, "gamehours.db"));

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
    public async Task EmptySnapshot_StillInitializesGameObservationBaseline()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Zero Unlock Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteAchievementRepository(database);

        Assert.False(await repository.HasObservedGameAsync(game.Id));

        var result = await repository.ApplySnapshotAsync(
            game.Id,
            Array.Empty<AchievementObservation>(),
            "CODEX local · estado parcial",
            hasCompleteCatalogue: false,
            DateTimeOffset.Parse("2026-08-21T15:00:00Z"));

        Assert.True(await repository.HasObservedGameAsync(game.Id));
        Assert.Empty(result.Current);
        Assert.Empty(result.NewlyUnlocked);
    }
}
