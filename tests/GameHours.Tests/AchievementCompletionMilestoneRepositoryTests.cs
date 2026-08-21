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

    [Fact]
    public async Task GetCompletionMilestones_ReturnsOnlyMilestonesInsideHalfOpenRange()
    {
        var database = Database;
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var writer = new SqliteAchievementRepository(database);
        var activity = new SqliteAchievementActivityRepository(database);

        var before = new TrackedGame(Guid.NewGuid(), "Before");
        var atStart = new TrackedGame(Guid.NewGuid(), "At Start");
        var inside = new TrackedGame(Guid.NewGuid(), "Inside");
        var atEnd = new TrackedGame(Guid.NewGuid(), "At End");
        foreach (var game in new[] { before, atStart, inside, atEnd })
        {
            await games.UpsertAsync(game);
        }

        await CompleteAtAsync(writer, before.Id, DateTimeOffset.Parse("2026-07-31T23:59:59Z"));
        await CompleteAtAsync(writer, atStart.Id, DateTimeOffset.Parse("2026-08-01T00:00:00Z"));
        await CompleteAtAsync(writer, inside.Id, DateTimeOffset.Parse("2026-08-21T12:30:00Z"));
        await CompleteAtAsync(writer, atEnd.Id, DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        var ranged = await activity.GetCompletionMilestonesAsync(
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        Assert.Equal(2, ranged.Count);
        Assert.Equal(atStart.Id, ranged[0].GameId);
        Assert.Equal(inside.Id, ranged[1].GameId);
    }

    [Fact]
    public async Task Initialize_BackfillsExistingCompletedCatalogueWhenMilestoneIsMissing()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Legacy Completed Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var finalUnlock = DateTimeOffset.Parse("2026-08-19T19:45:00Z");

        await new SqliteAchievementRepository(database).ApplySnapshotAsync(
            game.Id,
            new[]
            {
                Observation("ACH_FIRST", true, DateTimeOffset.Parse("2026-08-18T18:00:00Z")),
                Observation("ACH_FINAL", true, finalUnlock)
            },
            "Steam local stats",
            hasCompleteCatalogue: true,
            DateTimeOffset.Parse("2026-08-20T10:00:00Z"));

        await using (var connection = database.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM achievement_completion_milestones WHERE game_id = $game_id;";
            command.Parameters.AddWithValue("$game_id", game.Id.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.Empty(await new SqliteAchievementActivityRepository(database)
            .GetRecentCompletionMilestonesAsync());

        await database.InitializeAsync();

        var restored = Assert.Single(await new SqliteAchievementActivityRepository(database)
            .GetRecentCompletionMilestonesAsync());
        Assert.Equal(game.Id, restored.GameId);
        Assert.Equal(finalUnlock, restored.CompletedAtUtc);
        Assert.False(restored.IsObservedTimeFallback);
    }

    private static async Task CompleteAtAsync(
        SqliteAchievementRepository writer,
        Guid gameId,
        DateTimeOffset completedAtUtc)
    {
        await writer.ApplySnapshotAsync(
            gameId,
            new[] { Observation("ACH_ONLY", true, completedAtUtc) },
            "Steam local stats",
            hasCompleteCatalogue: true,
            completedAtUtc.AddMinutes(1));
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
