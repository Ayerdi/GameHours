using GameHours.Core.Domain;

namespace GameHours.Tests;

public sealed class AchievementUnlockEvidenceTests
{
    [Fact]
    public void Constructor_NormalizesAuditableFieldsAndTime()
    {
        var gameId = Guid.NewGuid();
        var localTime = new DateTimeOffset(2026, 8, 31, 2, 0, 0, TimeSpan.FromHours(2));

        var evidence = new ConfirmedAchievementUnlockEvidence(
            gameId,
            "  ACH_ONE  ",
            AchievementEvidenceOrigin.SaveGame,
            "  Gothic save  ",
            "  story.rule  ",
            ruleVersion: 2,
            sourcePath: "  C:\\Saves\\slot.sav  ",
            sourceFingerprint: "  sha256:abc  ",
            observedAtUtc: localTime,
            detail: "  Quest state is succeeded.  ");

        Assert.Equal(gameId, evidence.GameId);
        Assert.Equal("ACH_ONE", evidence.ApiName);
        Assert.Equal("Gothic save", evidence.Provider);
        Assert.Equal("story.rule", evidence.RuleId);
        Assert.Equal(@"C:\Saves\slot.sav", evidence.SourcePath);
        Assert.Equal("sha256:abc", evidence.SourceFingerprint);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T00:00:00Z"), evidence.ObservedAtUtc);
        Assert.Equal("Quest state is succeeded.", evidence.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveRuleVersion(int ruleVersion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConfirmedAchievementUnlockEvidence(
            Guid.NewGuid(),
            "ACH_ONE",
            AchievementEvidenceOrigin.SaveGame,
            "provider",
            "rule",
            ruleVersion,
            null,
            null,
            DateTimeOffset.UtcNow,
            "proof"));
    }

    [Fact]
    public void Constructor_RequiresExplanationForAutomaticProof()
    {
        Assert.Throws<ArgumentException>(() => new ConfirmedAchievementUnlockEvidence(
            Guid.NewGuid(),
            "ACH_ONE",
            AchievementEvidenceOrigin.SaveGame,
            "provider",
            "rule",
            1,
            null,
            null,
            DateTimeOffset.UtcNow,
            "   "));
    }
}
