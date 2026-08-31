using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Windows.Tests;

public sealed class AchievementEvidenceObservationServiceTests
{
    [Fact]
    public async Task ObserveAsync_PersistsNewProofAndProjectsOnlyCurrentRuleRevision()
    {
        var gameId = Guid.NewGuid();
        var repository = new InMemoryEvidenceRepository([
            Stored(gameId, "ACH_STORY", "save-provider", "story", 1)
        ]);
        var current = Proof(gameId, "ACH_STORY", "save-provider", "story", 2);
        var read = AchievementEvidenceReadResult.Success("save-provider", [current]) with
        {
            ActiveRuleIdentities = [new AchievementEvidenceRuleIdentity("save-provider", "ACH_STORY", "story", 2)]
        };
        var service = new AchievementEvidenceObservationService(
            new AchievementEvidenceProviderChain([new StubProvider("save-provider", read)]),
            repository);

        var result = await service.ObserveAsync(Request(gameId));

        Assert.Equal(2, result.AuditEvidence.Count);
        var active = Assert.Single(result.ActiveEvidence);
        Assert.Equal(2, active.RuleVersion);
        Assert.Equal("ACH_STORY", Assert.Single(result.ConfirmedApiNames));
        Assert.Single(repository.Saves);
    }

    [Fact]
    public async Task ObserveAsync_NoNewProofStillWithdrawsSupersededStoredRevision()
    {
        var gameId = Guid.NewGuid();
        var repository = new InMemoryEvidenceRepository([
            Stored(gameId, "ACH_STORY", "save-provider", "story", 1)
        ]);
        var read = AchievementEvidenceReadResult.NoEvidence("save-provider") with
        {
            ActiveRuleIdentities = [new AchievementEvidenceRuleIdentity("save-provider", "ACH_STORY", "story", 2)]
        };
        var service = new AchievementEvidenceObservationService(
            new AchievementEvidenceProviderChain([new StubProvider("save-provider", read)]),
            repository);

        var result = await service.ObserveAsync(Request(gameId));

        Assert.Single(result.AuditEvidence);
        Assert.Empty(result.ActiveEvidence);
        Assert.Empty(result.ConfirmedApiNames);
        Assert.Empty(repository.Saves);
    }

    [Fact]
    public async Task ObserveAsync_NotApplicableProviderDoesNotActivateStoredEvidence()
    {
        var gameId = Guid.NewGuid();
        var repository = new InMemoryEvidenceRepository([
            Stored(gameId, "ACH_STORY", "other-game", "story", 1)
        ]);
        var service = new AchievementEvidenceObservationService(
            new AchievementEvidenceProviderChain([
                new StubProvider("other-game", AchievementEvidenceReadResult.NotApplicable("other-game"))
            ]),
            repository);

        var result = await service.ObserveAsync(Request(gameId));

        Assert.Single(result.AuditEvidence);
        Assert.Empty(result.ActiveEvidence);
        Assert.Empty(result.ConfirmedApiNames);
    }

    [Fact]
    public async Task ObserveAsync_RepositoryCannotLeakEvidenceFromAnotherGameIntoProjection()
    {
        var gameId = Guid.NewGuid();
        var otherGameId = Guid.NewGuid();
        var repository = new InMemoryEvidenceRepository([
            Stored(otherGameId, "ACH_WRONG", "save-provider", "story", 2)
        ], ignoreGameFilter: true);
        var read = AchievementEvidenceReadResult.NoEvidence("save-provider") with
        {
            ActiveRuleIdentities = [new AchievementEvidenceRuleIdentity("save-provider", "ACH_WRONG", "story", 2)]
        };
        var service = new AchievementEvidenceObservationService(
            new AchievementEvidenceProviderChain([new StubProvider("save-provider", read)]),
            repository);

        var result = await service.ObserveAsync(Request(gameId));

        Assert.Empty(result.AuditEvidence);
        Assert.Empty(result.ActiveEvidence);
    }

    private static AchievementEvidenceRequest Request(Guid gameId) => new(
        gameId,
        "Example Game",
        @"C:\Games\Example\game.exe",
        "123456",
        DateTimeOffset.Parse("2026-08-31T12:00:00Z"));

    private static ConfirmedAchievementUnlockEvidence Proof(
        Guid gameId,
        string apiName,
        string provider,
        string ruleId,
        int version) => new(
            gameId,
            apiName,
            AchievementEvidenceOrigin.SaveGame,
            provider,
            ruleId,
            version,
            @"C:\Saves\slot.sav",
            "meta:test",
            DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
            "Persisted state proves the condition.");

    private static StoredAchievementUnlockEvidence Stored(
        Guid gameId,
        string apiName,
        string provider,
        string ruleId,
        int version) => new(
            gameId,
            apiName,
            AchievementEvidenceOrigin.SaveGame,
            provider,
            ruleId,
            version,
            @"C:\Saves\slot.sav",
            "meta:test",
            "Persisted state proves the condition.",
            DateTimeOffset.Parse("2026-08-31T11:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T11:00:00Z"));

    private sealed class StubProvider : IAchievementUnlockEvidenceProvider
    {
        private readonly AchievementEvidenceReadResult _result;

        public StubProvider(string name, AchievementEvidenceReadResult result)
        {
            Name = name;
            _result = result;
        }

        public string Name { get; }

        public Task<AchievementEvidenceReadResult> ReadAsync(
            AchievementEvidenceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class InMemoryEvidenceRepository : IAchievementEvidenceRepository
    {
        private readonly List<StoredAchievementUnlockEvidence> _stored;
        private readonly bool _ignoreGameFilter;

        public InMemoryEvidenceRepository(
            IEnumerable<StoredAchievementUnlockEvidence> initial,
            bool ignoreGameFilter = false)
        {
            _stored = initial.ToList();
            _ignoreGameFilter = ignoreGameFilter;
        }

        public List<IReadOnlyList<ConfirmedAchievementUnlockEvidence>> Saves { get; } = new();

        public Task SaveAsync(
            Guid gameId,
            IReadOnlyList<ConfirmedAchievementUnlockEvidence> evidence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saves.Add(evidence.ToArray());
            foreach (var proof in evidence)
            {
                _stored.Add(new StoredAchievementUnlockEvidence(
                    proof.GameId,
                    proof.ApiName,
                    proof.Origin,
                    proof.Provider,
                    proof.RuleId,
                    proof.RuleVersion,
                    proof.SourcePath,
                    proof.SourceFingerprint,
                    proof.Detail,
                    proof.ObservedAtUtc,
                    proof.ObservedAtUtc));
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredAchievementUnlockEvidence>> GetForGameAsync(
            Guid gameId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<StoredAchievementUnlockEvidence> result = _ignoreGameFilter
                ? _stored.ToArray()
                : _stored.Where(item => item.GameId == gameId).ToArray();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<StoredAchievementUnlockEvidence>>> GetForGamesAsync(
            IReadOnlyCollection<Guid> gameIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<Guid, IReadOnlyList<StoredAchievementUnlockEvidence>> result = gameIds
                .Distinct()
                .ToDictionary(
                    gameId => gameId,
                    gameId => (IReadOnlyList<StoredAchievementUnlockEvidence>)_stored
                        .Where(item => item.GameId == gameId)
                        .ToArray());
            return Task.FromResult(result);
        }
    }
}
