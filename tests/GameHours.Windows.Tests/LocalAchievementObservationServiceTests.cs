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
            hasObserved: false,
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
        Assert.True(repository.HasObservedCalled);
        Assert.True(repository.ApplyCalled);
    }

    [Fact]
    public async Task Observe_FirstGseSnapshotDoesNotPersistHistoricalSourceUnlockTimeAsExact()
    {
        var gameId = Guid.NewGuid();
        var unlocked = Stored(gameId, "ACH_OLD", isUnlocked: true);
        var repository = new StubRepository(
            hasObserved: false,
            applyResult: new AchievementApplyResult(new[] { unlocked }, new[] { unlocked }));
        var service = new LocalAchievementObservationService(
            new StubProvider(Snapshot(
                "ACH_OLD",
                unlocked: true,
                source: "GSE/Goldberg local")),
            repository);

        await service.ObserveAsync(
            gameId,
            @"C:\Games\Example\game.exe",
            DateTimeOffset.Parse("2026-08-21T14:00:00Z"));

        var observation = Assert.Single(repository.LastObservations!);
        Assert.True(observation.IsUnlocked);
        Assert.Null(observation.UnlockedAtUtc);
    }

    [Fact]
    public async Task Observe_LaterGseUnlockPreservesSourceUnlockTime()
    {
        var gameId = Guid.NewGuid();
        var unlockedAt = DateTimeOffset.Parse("2026-08-21T13:55:00Z");
        var unlocked = Stored(gameId, "ACH_NEW", isUnlocked: true) with
        {
            UnlockedAtUtc = unlockedAt
        };
        var repository = new StubRepository(
            hasObserved: true,
            applyResult: new AchievementApplyResult(new[] { unlocked }, new[] { unlocked }));
        var service = new LocalAchievementObservationService(
            new StubProvider(Snapshot(
                "ACH_NEW",
                unlocked: true,
                source: "GSE/Goldberg local",
                unlockedAtUtc: unlockedAt)),
            repository);

        await service.ObserveAsync(
            gameId,
            @"C:\Games\Example\game.exe",
            DateTimeOffset.Parse("2026-08-21T14:00:00Z"));

        var observation = Assert.Single(repository.LastObservations!);
        Assert.Equal(unlockedAt, observation.UnlockedAtUtc);
    }

    [Fact]
    public async Task Observe_LaterUnlockIsReturnedAsNotificationCandidate()
    {
        var gameId = Guid.NewGuid();
        var existing = Stored(gameId, "ACH_OLD", isUnlocked: true);
        var newlyUnlocked = Stored(gameId, "ACH_NEW", isUnlocked: true);
        var repository = new StubRepository(
            hasObserved: true,
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
            hasObserved: false,
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
        Assert.False(repository.HasObservedCalled);
        Assert.False(repository.ApplyCalled);
    }

    private static LocalAchievementSnapshot Snapshot(
        string apiName,
        bool unlocked,
        string source = "test source",
        DateTimeOffset? unlockedAtUtc = null) =>
        new(
            source,
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
                    UnlockedAtUtc: unlocked
                        ? unlockedAtUtc ?? DateTimeOffset.Parse("2026-08-21T13:55:00Z")
                        : null,
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
        private readonly bool _hasObserved;
        private readonly AchievementApplyResult _applyResult;

        public StubRepository(bool hasObserved, AchievementApplyResult applyResult)
        {
            _hasObserved = hasObserved;
            _applyResult = applyResult;
        }

        public bool HasObservedCalled { get; private set; }
        public bool ApplyCalled { get; private set; }
        public IReadOnlyList<AchievementObservation>? LastObservations { get; private set; }

        public Task<bool> HasObservedGameAsync(
            Guid gameId,
            CancellationToken cancellationToken = default)
        {
            HasObservedCalled = true;
            return Task.FromResult(_hasObserved);
        }

        public Task<AchievementApplyResult> ApplySnapshotAsync(
            Guid gameId,
            IReadOnlyList<AchievementObservation> observations,
            string source,
            bool hasCompleteCatalogue,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken = default)
        {
            ApplyCalled = true;
            LastObservations = observations;
            return Task.FromResult(_applyResult);
        }

        public Task<IReadOnlyList<StoredAchievement>> GetForGameAsync(
            Guid gameId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredAchievement>>(_applyResult.Current);
    }
}
