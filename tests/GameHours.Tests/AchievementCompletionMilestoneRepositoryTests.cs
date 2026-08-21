using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class AchievementCompletionMilestoneRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-completion-milestone-tests",
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
    public async Task PartialCatalogue_NeverCreatesCompletionMilestone()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Partial Game");
        await new SqliteGameRepository(database).UpsertAsync(game);

        await new SqliteAchievementRepository(database).ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_ONLY", true, DateTimeOffset.Parse("2026-08-20T20:00:00Z")) },
            "partial source",
            hasCompleteCatalogue: false,
            DateTimeOffset.Parse("2026-08-21T10:00:00Z"));

        var milestones = await new SqliteAchievementActivityRepository(database)
            .GetRecentCompletionMilestonesAsync();

        Assert.Empty(milestones);
    }

    [Fact]
    public async Task CompleteCatalogue_PersistsLatestUnlockAsExactCompletion()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Completed Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var finalUnlock = DateTimeOffset.Parse("2026-08-21T20:30:00Z");

        await new SqliteAchievementRepository(database).ApplySnapshotAsync(
            game.Id,
            new[]
            {
                Observation("ACH_FIRST", true, DateTimeOffset.Parse("2026-08-20T18:00:00Z")),
                Observation("ACH_FINAL", true, finalUnlock)
            },
            "Steam local stats",
            hasCompleteCatalogue: true,
            DateTimeOffset.Parse("2026-08-21T21:00:00Z"));

        var milestone = Assert.Single(
            await new SqliteAchievementActivityRepository(database)
                .GetRecentCompletionMilestonesAsync());

        Assert.Equal(game.Id, milestone.GameId);
        Assert.Equal("Completed Game", milestone.GameTitle);
        Assert.Equal(finalUnlock, milestone.CompletedAtUtc);
        Assert.False(milestone.IsObservedTimeFallback);
        Assert.Equal("Steam local stats", milestone.Source);
    }

    [Fact]
    public async Task ExactSteamTimestamp_ImprovesPreviouslyObservedCompletionTime()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Improved Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var writer = new SqliteAchievementRepository(database);
        var activity = new SqliteAchievementActivityRepository(database);
        var firstObservedCompletion = DateTimeOffset.Parse("2026-08-21T21:00:00Z");

        await writer.ApplySnapshotAsync(
            game.Id,
            new[]
            {
                Observation("ACH_FIRST", true, DateTimeOffset.Parse("2026-08-20T18:00:00Z")),
                Observation("ACH_FINAL", true, null)
            },
            "complete local catalogue",
            hasCompleteCatalogue: true,
            firstObservedCompletion);

        var approximate = Assert.Single(await activity.GetRecentCompletionMilestonesAsync());
        Assert.Equal(firstObservedCompletion, approximate.CompletedAtUtc);
        Assert.True(approximate.IsObservedTimeFallback);

        var exactFinalUnlock = DateTimeOffset.Parse("2026-08-21T20:30:00Z");
        await writer.ApplySnapshotAsync(
            game.Id,
            new[]
            {
                Observation("ACH_FIRST", true, DateTimeOffset.Parse("2026-08-20T18:00:00Z")),
                Observation("ACH_FINAL", true, exactFinalUnlock)
            },
            "Steam local stats",
            hasCompleteCatalogue: true,
            DateTimeOffset.Parse("2026-08-21T22:00:00Z"));

        var improved = Assert.Single(await activity.GetRecentCompletionMilestonesAsync());
        Assert.Equal(exactFinalUnlock, improved.CompletedAtUtc);
        Assert.False(improved.IsObservedTimeFallback);
        Assert.Equal("Steam local stats", improved.Source);
    }

    private static AchievementObservation Observation(
        string apiName,
        bool unlocked,
        DateTimeOffset? unlockedAtUtc) =>
        new(
            apiName,
            apiName,
            $"Description for {apiName}",
            Hidden: false,
            IsUnlocked: unlocked,
            UnlockedAtUtc: unlockedAtUtc);
}
