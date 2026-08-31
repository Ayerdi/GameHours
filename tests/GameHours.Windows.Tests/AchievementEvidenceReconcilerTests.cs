using GameHours.Core.Domain;
using GameHours.Windows.Achievements;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Windows.Tests;

public sealed class AchievementEvidenceReconcilerTests
{
    [Fact]
    public void Reconcile_IncompletePrimaryAddsOnlyPositiveSupplementalEvidence()
    {
        var gameId = Guid.NewGuid();
        var snapshot = Snapshot(
            ("ACH_ONE", true),
            ("ACH_TWO", false));
        var evidence = new[]
        {
            Evidence(gameId, "ACH_TWO"),
            Evidence(gameId, "ACH_THREE")
        };

        var projection = AchievementEvidenceReconciler.Reconcile(
            gameId,
            snapshot,
            AchievementStateCoverage.UnlocksOnly,
            evidence,
            new[] { Active("ACH_TWO"), Active("ACH_THREE") });

        Assert.False(projection.IsExact);
        Assert.Equal(new[] { "ACH_ONE", "ACH_THREE", "ACH_TWO" }, projection.ConfirmedApiNames);
        Assert.Equal(3, projection.ConfirmedCount);
        Assert.Equal(2, projection.SupplementalConfirmedCount);
    }

    [Fact]
    public void Reconcile_CompletePrimaryDoesNotLetSupplementalEvidenceOverrideLockedState()
    {
        var gameId = Guid.NewGuid();
        var snapshot = Snapshot(
            ("ACH_ONE", true),
            ("ACH_TWO", false));

        var projection = AchievementEvidenceReconciler.Reconcile(
            gameId,
            snapshot,
            AchievementStateCoverage.Complete,
            new[] { Evidence(gameId, "ACH_TWO") },
            Array.Empty<AchievementEvidenceRuleIdentity>());

        Assert.True(projection.IsExact);
        Assert.Equal("ACH_ONE", Assert.Single(projection.ConfirmedApiNames));
        Assert.Equal(0, projection.SupplementalConfirmedCount);
    }

    [Fact]
    public void Reconcile_EvidenceFromAnotherGameIsIgnored()
    {
        var gameId = Guid.NewGuid();

        var projection = AchievementEvidenceReconciler.Reconcile(
            gameId,
            primarySnapshot: null,
            AchievementStateCoverage.Unknown,
            new[] { Evidence(Guid.NewGuid(), "ACH_WRONG") },
            new[] { Active("ACH_WRONG") });

        Assert.False(projection.IsExact);
        Assert.Empty(projection.ConfirmedApiNames);
    }

    [Fact]
    public void Reconcile_DuplicateProofsDoNotInflateConfirmedCount()
    {
        var gameId = Guid.NewGuid();
        var evidence = new[]
        {
            Evidence(gameId, "ACH_ONE", "rule-a"),
            Evidence(gameId, "ach_one", "rule-b")
        };

        var projection = AchievementEvidenceReconciler.Reconcile(
            gameId,
            primarySnapshot: null,
            AchievementStateCoverage.Unknown,
            evidence,
            new[]
            {
                Active("ACH_ONE", "rule-a"),
                Active("ACH_ONE", "rule-b")
            });

        Assert.Equal(1, projection.ConfirmedCount);
        Assert.Equal(1, projection.SupplementalConfirmedCount);
    }

    [Fact]
    public void Reconcile_SupersededRuleVersionCannotKeepFalsePositiveAlive()
    {
        var gameId = Guid.NewGuid();
        var staleEvidence = Evidence(
            gameId,
            "ACH_STORY",
            ruleId: "story.completed",
            ruleVersion: 1);

        var projection = AchievementEvidenceReconciler.Reconcile(
            gameId,
            primarySnapshot: null,
            AchievementStateCoverage.Unknown,
            new[] { staleEvidence },
            new[] { Active("ACH_STORY", "story.completed", version: 2) });

        Assert.False(projection.IsExact);
        Assert.Empty(projection.ConfirmedApiNames);
        Assert.Equal(0, projection.SupplementalConfirmedCount);
    }

    [Fact]
    public void Reconcile_RemovedRuleCannotContributeToProjection()
    {
        var gameId = Guid.NewGuid();
        var staleEvidence = Evidence(gameId, "ACH_REMOVED", "removed.rule");

        var projection = AchievementEvidenceReconciler.Reconcile(
            gameId,
            primarySnapshot: null,
            AchievementStateCoverage.Unknown,
            new[] { staleEvidence },
            Array.Empty<AchievementEvidenceRuleIdentity>());

        Assert.Empty(projection.ConfirmedApiNames);
    }

    private static LocalAchievementSnapshot Snapshot(params (string ApiName, bool Unlocked)[] achievements) =>
        new(
            "test",
            "123",
            "definitions.json",
            "state.json",
            achievements.Select(item => new LocalAchievement(
                item.ApiName,
                item.ApiName,
                string.Empty,
                Hidden: false,
                IsUnlocked: item.Unlocked,
                UnlockedAtUtc: null,
                IconPath: null,
                LockedIconPath: null,
                Progress: null,
                MaxProgress: null)).ToArray())
        {
            IsCatalogueComplete = true
        };

    private static ConfirmedAchievementUnlockEvidence Evidence(
        Guid gameId,
        string apiName,
        string ruleId = "rule",
        int ruleVersion = 1) =>
        new(
            gameId,
            apiName,
            AchievementEvidenceOrigin.SaveGame,
            "test-provider",
            ruleId,
            ruleVersion,
            sourcePath: @"C:\Saves\slot.sav",
            sourceFingerprint: "sha256:test",
            observedAtUtc: DateTimeOffset.Parse("2026-08-31T00:45:00Z"),
            detail: "Test proof.");

    private static AchievementEvidenceRuleIdentity Active(
        string apiName,
        string ruleId = "rule",
        int version = 1) =>
        new("test-provider", apiName, ruleId, version);
}
