using GameHours.Core.Domain;

namespace GameHours.Tests;

public sealed class AchievementEvidenceRulePolicyTests
{
    [Fact]
    public void KeepActive_StoredSupersededRevisionRemainsAuditableButIsNotEffective()
    {
        var gameId = Guid.NewGuid();
        var first = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var stored = new[]
        {
            Stored(gameId, "ACH_STORY", "save-provider", "story.completed", 1, first),
            Stored(gameId, "ACH_STORY", "save-provider", "story.completed", 2, first.AddMinutes(5))
        };

        var effective = AchievementEvidenceRulePolicy.KeepActive(
            stored,
            new[]
            {
                new AchievementEvidenceRuleIdentity(
                    "SAVE-PROVIDER",
                    "ach_story",
                    "STORY.COMPLETED",
                    2)
            });

        Assert.Equal(2, stored.Length);
        var current = Assert.Single(effective);
        Assert.Equal(2, current.RuleVersion);
        Assert.Contains(stored, item => item.RuleVersion == 1);
    }

    [Fact]
    public void KeepActive_RemovedRuleFailsClosedWithoutDeletingAuditEvidence()
    {
        var gameId = Guid.NewGuid();
        var stored = new[]
        {
            Stored(
                gameId,
                "ACH_REMOVED",
                "save-provider",
                "removed.rule",
                1,
                DateTimeOffset.Parse("2026-08-31T00:00:00Z"))
        };

        var effective = AchievementEvidenceRulePolicy.KeepActive(
            stored,
            Array.Empty<AchievementEvidenceRuleIdentity>());

        Assert.Empty(effective);
        Assert.Single(stored);
    }

    private static StoredAchievementUnlockEvidence Stored(
        Guid gameId,
        string apiName,
        string provider,
        string ruleId,
        int ruleVersion,
        DateTimeOffset observedAtUtc) =>
        new(
            gameId,
            apiName,
            AchievementEvidenceOrigin.SaveGame,
            provider,
            ruleId,
            ruleVersion,
            SourcePath: @"C:\Saves\slot.sav",
            SourceFingerprint: "meta:test",
            Detail: "Test proof.",
            FirstObservedAtUtc: observedAtUtc,
            LastObservedAtUtc: observedAtUtc);
}
