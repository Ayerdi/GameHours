using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteAchievementRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-achievement-tests",
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
    public async Task ApplySnapshot_DetectsOnlyTheFirstLockedToUnlockedTransition()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Achievement Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteAchievementRepository(database);
        var firstSeen = DateTimeOffset.Parse("2026-08-21T10:00:00Z");

        var locked = await repository.ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_ONE", unlocked: false) },
            "catalogue",
            hasCompleteCatalogue: true,
            firstSeen);
        Assert.Empty(locked.NewlyUnlocked);

        var unlockedAt = firstSeen.AddMinutes(10);
        var unlocked = await repository.ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_ONE", unlocked: true, unlockedAt) },
            "local state",
            hasCompleteCatalogue: false,
            firstSeen.AddMinutes(11));
        var newUnlock = Assert.Single(unlocked.NewlyUnlocked);
        Assert.True(newUnlock.IsUnlocked);
        Assert.Equal(unlockedAt, newUnlock.UnlockedAtUtc);

        var repeated = await repository.ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_ONE", unlocked: true, unlockedAt) },
            "local state",
            hasCompleteCatalogue: false,
            firstSeen.AddMinutes(12));
        Assert.Empty(repeated.NewlyUnlocked);
    }

    [Fact]
    public async Task ApplySnapshot_PartialStateCannotRelockOrEraseRichMetadata()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Metadata Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteAchievementRepository(database);
        var observedAt = DateTimeOffset.Parse("2026-08-21T11:00:00Z");

        await repository.ApplySnapshotAsync(
            game.Id,
            new[]
            {
                new AchievementObservation(
                    "ACH_SECRET",
                    "A proper title",
                    "A proper description",
                    Hidden: true,
                    IsUnlocked: true,
                    UnlockedAtUtc: observedAt.AddMinutes(-5))
            },
            "complete catalogue",
            hasCompleteCatalogue: true,
            observedAt);

        await repository.ApplySnapshotAsync(
            game.Id,
            new[]
            {
                new AchievementObservation(
                    "ACH_SECRET",
                    "ACH_SECRET",
                    string.Empty,
                    Hidden: false,
                    IsUnlocked: false,
                    UnlockedAtUtc: null)
            },
            "partial state",
            hasCompleteCatalogue: false,
            observedAt.AddMinutes(1));

        var stored = Assert.Single(await repository.GetForGameAsync(game.Id));
        Assert.True(stored.IsUnlocked);
        Assert.True(stored.Hidden);
        Assert.Equal("A proper title", stored.DisplayName);
        Assert.Equal("A proper description", stored.Description);
    }

    [Fact]
    public async Task ApplySnapshot_PreservesEarliestKnownUnlockTime()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Timestamp Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteAchievementRepository(database);
        var later = DateTimeOffset.Parse("2026-08-21T12:30:00Z");
        var earlier = later.AddHours(-1);

        await repository.ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_TIME", unlocked: true, later) },
            "source one",
            hasCompleteCatalogue: false,
            later);
        await repository.ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_TIME", unlocked: true, earlier) },
            "source two",
            hasCompleteCatalogue: false,
            later.AddMinutes(1));

        var stored = Assert.Single(await repository.GetForGameAsync(game.Id));
        Assert.Equal(earlier, stored.UnlockedAtUtc);
        Assert.Equal(later, stored.FirstUnlockedSeenAtUtc);
    }

    [Fact]
    public async Task ApplySnapshot_FirstObservationOfUnlockedAchievementIsReportedAsNew()
    {
        var database = Database;
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "Baseline Game");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteAchievementRepository(database);
        var observedAt = DateTimeOffset.Parse("2026-08-21T13:00:00Z");

        var result = await repository.ApplySnapshotAsync(
            game.Id,
            new[] { Observation("ACH_EXISTING", unlocked: true, observedAt.AddDays(-2)) },
            "initial local import",
            hasCompleteCatalogue: false,
            observedAt);

        Assert.Single(result.NewlyUnlocked);
        Assert.Equal(observedAt, Assert.Single(result.Current).FirstUnlockedSeenAtUtc);
    }

    private static AchievementObservation Observation(
        string apiName,
        bool unlocked,
        DateTimeOffset? unlockedAt = null) =>
        new(
            apiName,
            apiName,
            string.Empty,
            Hidden: false,
            IsUnlocked: unlocked,
            UnlockedAtUtc: unlockedAt);
}
