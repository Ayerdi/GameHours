using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class LocalAchievementObservationServiceTests
{
    [Fact]
    public async Task Observe_FirstSnapshotIsBaselineAndSuppressesHistoricalNotifications()
    {
        var gameId = Guid.NewGuid();
        var unlocked = Stored(gameId, "ACH_ONE", isUnlocked: true);
        var repository = new StubRepository(
            existing: Array.Empty<StoredAchievement>(),
            applyResult: new AchievementApplyResult(new[] { unlocked }, new[] { unlocked }));
        var service = new LocalAchievementObservationService(
            new StubProvider(Snapshot("ACH_ONE", unlocked: true)),
            repository);

        var result = await service.ObserveAsync(
            gameId,
            @"C:\Games\Example\game.exe",
            DateTimeOffset.Parse("2026-08-21T14:00:00Z"));

        Assert.NotNull(result);
        Assert.True(result.IsBaseline);
        Assert.Empty(result.NotificationCandidates);
        Assert.True(repository.ApplyCalled);
    }

    [Fact]
    public async Task Observe_LaterUnlockIsReturnedAsNotificationCandidate()
    {
        var gameId = Guid.NewGuid();
        var existing = Stored(gameId, "ACH_OLD", isUnlocked: true);
        var newlyUnlocked = Stored(gameId, "ACH_NEW", isUnlocked: true);
        var repository = new StubRepository(
            existing: new[] { existing },
            applyResult: new AchievementApplyResult(
                new[] { existing, newlyUnlocked },
                new[] { newlyUnlocked }));
        var service = new LocalAchievementObservationService(
            new StubProvider(Snapshot("ACH_NEW", unlocked: true)),
            repository);

        var result = await service.ObserveAsync(
            gameId,
            @"C:\Games\Example\game.exe",
            DateTimeOffset.Parse("2026-08-21T14:05:00Z"));

        Assert.NotNull(result);
        Assert.False(result.IsBaseline);
        Assert.Equal("ACH_NEW", Assert.Single(result.NotificationCandidates).ApiName);
    }

    [Fact]
    public async Task Observe_NoReadableSnapshotDoesNotTouchPersistence()
    {
        var repository = new StubRepository(
            existing: Array.Empty<StoredAchievement>(),
            applyResult: new AchievementApplyResult(
                Array.Empty<StoredAchievement>(),
                Array.Empty<StoredAchievement>()));
        var service = new LocalAchievementObservationService(
            new StubProvider(null),
            repository);

        var result = await service.ObserveAsync(
            Guid.NewGuid(),
            @"C:\Games\Example\game.exe",
            DateTimeOffset.UtcNow);

        Assert.Null(result);
        Assert.False(repository.ApplyCalled);
        Assert.False(repository.GetCalled);
    }

    private static LocalAchievementSnapshot Snapshot(string apiName, bool unlocked) =>
        new(
            "test source",
            "123456",
            "definitions.json",
            "state.json",
            new[]
            {
                new LocalAchievement(
                    apiName,
                    apiName,
                    string.Empty,
                    Hidden: false,
                    IsUnlocked: unlocked,
                    UnlockedAtUtc: unlocked ? DateTimeOffset.Parse("2026-08-21T13:55:00Z") : null,
                    IconPath: null,
                    LockedIconPath: null,
                    Progress: null,
                    MaxProgress: null)
            });

    private static StoredAchievement Stored(Guid gameId, string apiName, bool isUnlocked) =>
        new(
            gameId,
            apiName,
            apiName,
            string.Empty,
            Hidden: false,
            IsUnlocked: isUnlocked,
            UnlockedAtUtc: isUnlocked ? DateTimeOffset.Parse("2026-08-21T13:55:00Z") : null,
            Source: "test source",
            FirstSeenAtUtc: DateTimeOffset.Parse("2026-08-21T13:50:00Z"),
            LastSeenAtUtc: DateTimeOffset.Parse("2026-08-21T14:00:00Z"),
            FirstUnlockedSeenAtUtc: isUnlocked ? DateTimeOffset.Parse("2026-08-21T14:00:00Z") : null);

    private sealed class StubProvider : ILocalAchievementProvider
    {
        private readonly LocalAchievementSnapshot? _snapshot;

        public StubProvider(LocalAchievementSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public string Name => "stub";

        public LocalAchievementSnapshot? TryRead(string executablePath) => _snapshot;
    }

    private sealed class StubRepository : IAchievementRepository
    {
        private readonly IReadOnlyList<StoredAchievement> _existing;
        private readonly AchievementApplyResult _applyResult;

        public StubRepository(
            IReadOnlyList<StoredAchievement> existing,
            AchievementApplyResult applyResult)
        {
            _existing = existing;
            _applyResult = applyResult;
        }

        public bool GetCalled { get; private set; }
        public bool ApplyCalled { get; private set; }

        public Task<AchievementApplyResult> ApplySnapshotAsync(
            Guid gameId,
            IReadOnlyList<AchievementObservation> observations,
            string source,
            bool hasCompleteCatalogue,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            ApplyCalled = true;
            return Task.FromResult(_applyResult);
        }

        public Task<IReadOnlyList<StoredAchievement>> GetForGameAsync(
            Guid gameId,
            CancellationToken cancellationToken = default)
        {
            GetCalled = true;
            return Task.FromResult(_existing);
        }
    }
}
