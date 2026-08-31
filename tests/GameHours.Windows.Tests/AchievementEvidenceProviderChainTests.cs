using GameHours.Core.Domain;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Windows.Tests;

public sealed class AchievementEvidenceProviderChainTests
{
    [Fact]
    public async Task ReadAsync_AggregatesIndependentPositiveEvidence()
    {
        var gameId = Guid.NewGuid();
        var request = Request(gameId);
        var first = Evidence(gameId, "ACH_ONE", "provider-a", "rule-one");
        var second = Evidence(gameId, "ACH_TWO", "provider-b", "rule-two");
        var chain = new AchievementEvidenceProviderChain(new IAchievementUnlockEvidenceProvider[]
        {
            StubProvider.Success("provider-a", first),
            StubProvider.Success("provider-b", second)
        });

        var result = await chain.ReadAsync(request);

        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(new[] { "ACH_ONE", "ACH_TWO" }, result.ConfirmedApiNames);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ReadAsync_PreservesMultipleProofsButCountsAchievementOnce()
    {
        var gameId = Guid.NewGuid();
        var chain = new AchievementEvidenceProviderChain(new IAchievementUnlockEvidenceProvider[]
        {
            StubProvider.Success("save-a", Evidence(gameId, "ACH_ONE", "save-a", "quest-proof")),
            StubProvider.Success("save-b", Evidence(gameId, "ACH_ONE", "save-b", "story-proof"))
        });

        var result = await chain.ReadAsync(Request(gameId));

        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal("ACH_ONE", Assert.Single(result.ConfirmedApiNames));
    }

    [Fact]
    public async Task ReadAsync_FailedProviderDoesNotEraseSuccessfulEvidence()
    {
        var gameId = Guid.NewGuid();
        var chain = new AchievementEvidenceProviderChain(new IAchievementUnlockEvidenceProvider[]
        {
            StubProvider.Failure("broken", "Save format is unsupported."),
            StubProvider.Success("healthy", Evidence(gameId, "ACH_ONE", "healthy", "proof"))
        });

        var result = await chain.ReadAsync(Request(gameId));

        Assert.Equal("ACH_ONE", Assert.Single(result.ConfirmedApiNames));
        Assert.True(result.HasFailures);
        Assert.Equal("Save format is unsupported.", Assert.Single(result.Diagnostics).Detail);
    }

    [Fact]
    public async Task ReadAsync_RejectsEvidenceForDifferentGame()
    {
        var requestedGameId = Guid.NewGuid();
        var wrongGameId = Guid.NewGuid();
        var chain = new[]
        {
            StubProvider.Success("bad-provider", Evidence(wrongGameId, "ACH_ONE", "bad-provider", "proof"))
        };

        var result = await new AchievementEvidenceProviderChain(chain).ReadAsync(Request(requestedGameId));

        Assert.Empty(result.Evidence);
        Assert.Empty(result.ConfirmedApiNames);
        Assert.Contains(requestedGameId.ToString("D"), Assert.Single(result.Diagnostics).Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_NotApplicableAndNoEvidenceRemainNeutral()
    {
        var gameId = Guid.NewGuid();
        var chain = new AchievementEvidenceProviderChain(new IAchievementUnlockEvidenceProvider[]
        {
            new StubProvider("other-game", _ => AchievementEvidenceReadResult.NotApplicable("other-game")),
            new StubProvider("empty-save", _ => AchievementEvidenceReadResult.NoEvidence("empty-save"))
        });

        var result = await chain.ReadAsync(Request(gameId));

        Assert.Empty(result.Evidence);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.ActiveRuleIdentities);
        Assert.False(result.HasFailures);
    }

    [Fact]
    public async Task ReadAsync_AggregatesActiveRulesFromApplicableProviderWithoutPositiveEvidence()
    {
        var gameId = Guid.NewGuid();
        var active = new AchievementEvidenceRuleIdentity(
            "save-provider",
            "ACH_CURRENT",
            "quest.current",
            2);
        var ignored = new AchievementEvidenceRuleIdentity(
            "other-provider",
            "ACH_OTHER",
            "quest.other",
            1);
        var chain = new AchievementEvidenceProviderChain(new IAchievementUnlockEvidenceProvider[]
        {
            new StubProvider(
                "save-provider",
                _ => AchievementEvidenceReadResult.NoEvidence("save-provider") with
                {
                    ActiveRuleIdentities = new[] { active }
                }),
            new StubProvider(
                "other-provider",
                _ => AchievementEvidenceReadResult.NotApplicable("other-provider") with
                {
                    ActiveRuleIdentities = new[] { ignored }
                })
        });

        var result = await chain.ReadAsync(Request(gameId));

        Assert.Equal(active, Assert.Single(result.ActiveRuleIdentities));
        Assert.Empty(result.Evidence);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ReadAsync_RejectsActiveRuleClaimedUnderAnotherProvider()
    {
        var gameId = Guid.NewGuid();
        var chain = new AchievementEvidenceProviderChain(new IAchievementUnlockEvidenceProvider[]
        {
            new StubProvider(
                "provider-a",
                _ => AchievementEvidenceReadResult.NoEvidence("provider-a") with
                {
                    ActiveRuleIdentities = new[]
                    {
                        new AchievementEvidenceRuleIdentity(
                            "provider-b",
                            "ACH_WRONG",
                            "wrong.rule",
                            1)
                    }
                })
        });

        var result = await chain.ReadAsync(Request(gameId));

        Assert.Empty(result.ActiveRuleIdentities);
        Assert.Contains("unrelated provider", Assert.Single(result.Diagnostics).Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static AchievementEvidenceRequest Request(Guid gameId) =>
        new(
            gameId,
            "Example Game",
            @"C:\Games\Example\game.exe",
            "123456",
            DateTimeOffset.Parse("2026-08-31T00:30:00Z"));

    private static ConfirmedAchievementUnlockEvidence Evidence(
        Guid gameId,
        string apiName,
        string provider,
        string ruleId) =>
        new(
            gameId,
            apiName,
            AchievementEvidenceOrigin.SaveGame,
            provider,
            ruleId,
            ruleVersion: 1,
            sourcePath: @"C:\Saves\slot1.sav",
            sourceFingerprint: "sha256:abc",
            observedAtUtc: DateTimeOffset.Parse("2026-08-31T00:30:00Z"),
            detail: "Persistent quest state proves the unlock condition.");

    private sealed class StubProvider : IAchievementUnlockEvidenceProvider
    {
        private readonly Func<AchievementEvidenceRequest, AchievementEvidenceReadResult> _read;

        public StubProvider(
            string name,
            Func<AchievementEvidenceRequest, AchievementEvidenceReadResult> read)
        {
            Name = name;
            _read = read;
        }

        public string Name { get; }

        public Task<AchievementEvidenceReadResult> ReadAsync(
            AchievementEvidenceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_read(request));
        }

        public static StubProvider Success(
            string name,
            params ConfirmedAchievementUnlockEvidence[] evidence) =>
            new(name, _ => AchievementEvidenceReadResult.Success(name, evidence));

        public static StubProvider Failure(string name, string detail) =>
            new(name, _ => AchievementEvidenceReadResult.Failure(name, detail));
    }
}
