using GameHours.Core.Domain;

namespace GameHours.Tests;

public sealed class AchievementGameSummaryTests
{
    [Fact]
    public void CompleteCatalogue_AllUnlocked_IsComplete()
    {
        var summary = Summary(
            knownCount: 23,
            unlockedCount: 23,
            hasCompleteCatalogue: true);

        Assert.True(summary.IsComplete);
        Assert.Equal(100d, summary.CompletionPercentage);
    }

    [Fact]
    public void PartialState_NeverClaimsCompletion()
    {
        var summary = Summary(
            knownCount: 10,
            unlockedCount: 10,
            hasCompleteCatalogue: false);

        Assert.False(summary.IsComplete);
        Assert.Null(summary.CompletionPercentage);
    }

    [Fact]
    public void EmptyCompleteCatalogue_DoesNotCountAsCompletedGame()
    {
        var summary = Summary(
            knownCount: 0,
            unlockedCount: 0,
            hasCompleteCatalogue: true);

        Assert.False(summary.IsComplete);
        Assert.Null(summary.CompletionPercentage);
    }

    [Fact]
    public void CompletionPercentage_UsesCompleteCatalogueTotal()
    {
        var summary = Summary(
            knownCount: 23,
            unlockedCount: 4,
            hasCompleteCatalogue: true);

        Assert.False(summary.IsComplete);
        Assert.NotNull(summary.CompletionPercentage);
        Assert.Equal(4 * 100d / 23d, summary.CompletionPercentage!.Value, precision: 8);
    }

    private static AchievementGameSummary Summary(
        int knownCount,
        int unlockedCount,
        bool hasCompleteCatalogue) =>
        new(
            Guid.NewGuid(),
            knownCount,
            unlockedCount,
            hasCompleteCatalogue,
            FirstUnlockedAtUtc: null,
            LastUnlockedAtUtc: null,
            LastObservedAtUtc: null,
            LastSource: null);
}
