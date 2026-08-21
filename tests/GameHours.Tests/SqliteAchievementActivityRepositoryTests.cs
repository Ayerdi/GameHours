using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteAchievementActivityRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-achievement-activity-tests",
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
    public async Task GetSummary_CompleteCatalogueReportsCountsAndUnlockBounds()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Summary Game");
        await new SqliteGameRepository(database).UpsertAsync(game);

        var firstUnlock = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        var lastUnlock = DateTimeOffset.Parse("2026-08-21T11:00:00Z");
        var observedAt = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
        var writer = new SqliteAchievementRepository(database);
        await writer.ApplySnapshotAsync(
            game.Id,
            new[]
            {
                Observation("ACH_FIRST", "First", true, firstUnlock),
                Observation("ACH_LAST", "Last", true, lastUnlock),
                Observation("ACH_LOCKED", "Locked", false, null)
            },
            "complete catalogue",
            hasCompleteCatalogue: true,
            observedAt);

        var summary = await new SqliteAchievementActivityRepository(database)
            .GetSummaryAsync(game.Id);

        Assert.NotNull(summary);
        Assert.Equal(3, summary.KnownCount);
        Assert.Equal(2, summary.UnlockedCount);
        Assert.True(summary.HasCompleteCatalogue);
        Assert.Equal(firstUnlock, summary.FirstUnlockedAtUtc);
        Assert.Equal(lastUnlock, summary.LastUnlockedAtUtc);
        Assert.Equal(observedAt, summary.LastObservedAtUtc);
        Assert.Equal("complete catalogue", summary.LastSource);
    }

    [Fact]
    public async Task GetSummary_PartialStateUsesObservedTimeWhenSourceHasNoUnlockTimestamp()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Partial Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var observedAt = DateTimeOffset.Parse("2026-08-21T13:00:00Z");

        await new SqliteAchievementRepository(database).ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_NO_TIME", "No time", true, null) },
            "partial state",
            hasCompleteCatalogue: false,
            observedAt);

        var summary = await new SqliteAchievementActivityRepository(database)
            .GetSummaryAsync(game.Id);

        Assert.NotNull(summary);
        Assert.Equal(1, summary.KnownCount);
        Assert.Equal(1, summary.UnlockedCount);
        Assert.False(summary.HasCompleteCatalogue);
        Assert.Equal(observedAt, summary.FirstUnlockedAtUtc);
        Assert.Equal(observedAt, summary.LastUnlockedAtUtc);
    }

    [Fact]
    public async Task GetRecentUnlocks_OrdersByBestKnownOccurrenceAndMarksFallbackTime()
    {
        var database = Database;
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var writer = new SqliteAchievementRepository(database);
        var activity = new SqliteAchievementActivityRepository(database);

        var exactGame = new TrackedGame(Guid.NewGuid(), "Exact Game");
        var observedGame = new TrackedGame(Guid.NewGuid(), "Observed Game");
        await games.UpsertAsync(exactGame);
        await games.UpsertAsync(observedGame);

        var exactUnlock = DateTimeOffset.Parse("2026-08-21T13:00:00Z");
        await writer.ApplySnapshotAsync(
            exactGame.Id,
            new[] { Observation("ACH_EXACT", "Exact unlock", true, exactUnlock) },
            "exact source",
            hasCompleteCatalogue: true,
            DateTimeOffset.Parse("2026-08-21T14:00:00Z"));

        var observedAt = DateTimeOffset.Parse("2026-08-21T15:00:00Z");
        await writer.ApplySnapshotAsync(
            observedGame.Id,
            new[] { Observation("ACH_OBSERVED", "Observed unlock", true, null) },
            "timestamp-less source",
            hasCompleteCatalogue: false,
            observedAt);

        var recent = await activity.GetRecentUnlocksAsync(limit: 10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("ACH_OBSERVED", recent[0].ApiName);
        Assert.Equal(observedAt, recent[0].OccurredAtUtc);
        Assert.True(recent[0].IsObservedTimeFallback);
        Assert.Equal("Observed Game", recent[0].GameTitle);

        Assert.Equal("ACH_EXACT", recent[1].ApiName);
        Assert.Equal(exactUnlock, recent[1].OccurredAtUtc);
        Assert.False(recent[1].IsObservedTimeFallback);
    }

    [Fact]
    public async Task GetRecentUnlocks_CanFilterToOneGame()
    {
        var database = Database;
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var writer = new SqliteAchievementRepository(database);
        var activity = new SqliteAchievementActivityRepository(database);
        var first = new TrackedGame(Guid.NewGuid(), "First Game");
        var second = new TrackedGame(Guid.NewGuid(), "Second Game");
        await games.UpsertAsync(first);
        await games.UpsertAsync(second);

        await writer.ApplySnapshotAsync(
            first.Id,
            new[] { Observation("ACH_FIRST", "First", true, null) },
            "source",
            false,
            DateTimeOffset.Parse("2026-08-21T14:00:00Z"));
        await writer.ApplySnapshotAsync(
            second.Id,
            new[] { Observation("ACH_SECOND", "Second", true, null) },
            "source",
            false,
            DateTimeOffset.Parse("2026-08-21T15:00:00Z"));

        var filtered = await activity.GetRecentUnlocksAsync(limit: 10, gameId: first.Id);

        var item = Assert.Single(filtered);
        Assert.Equal(first.Id, item.GameId);
        Assert.Equal("ACH_FIRST", item.ApiName);
    }

    private static AchievementObservation Observation(
        string apiName,
        string displayName,
        bool unlocked,
        DateTimeOffset? unlockedAtUtc) =>
        new(
            apiName,
            displayName,
            $"Description for {apiName}",
            Hidden: false,
            IsUnlocked: unlocked,
            UnlockedAtUtc: unlockedAtUtc);
}
