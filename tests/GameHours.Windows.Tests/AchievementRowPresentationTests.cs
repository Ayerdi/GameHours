using GameHours.Desktop;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class AchievementRowPresentationTests
{
    [Fact]
    public void HistoricalTimestampCanBeHiddenWhenItIsNotVerified()
    {
        var model = CreateModel(DateTimeOffset.Parse("2026-08-29T15:52:00Z"));
        var row = new GameDetailView.AchievementRowViewModel(
            model,
            historicalTimeUnverified: true);

        Assert.Equal("Desbloqueado · hora histórica no disponible", row.StatusText);
    }

    [Fact]
    public void TimestampCanBeSuppressedUntilPersistenceEvidenceLoads()
    {
        var model = CreateModel(DateTimeOffset.Parse("2026-08-29T15:52:00Z"));
        var row = new GameDetailView.AchievementRowViewModel(
            model,
            suppressUnlockTime: true);

        Assert.Equal("Desbloqueado", row.StatusText);
    }

    [Fact]
    public void VerifiedTimestampIsStillPresented()
    {
        var model = CreateModel(DateTimeOffset.Now);
        var row = new GameDetailView.AchievementRowViewModel(model);

        Assert.StartsWith("Desbloqueado · ", row.StatusText);
    }

    [Fact]
    public void UnlocksOnlyEmptyState_DoesNotClaimZeroHistoricalUnlocks()
    {
        Assert.Equal(
            "?/42",
            GameDetailView.FormatLiveAchievementCount(
                unlocked: 0,
                total: 42,
                partialCatalogue: false,
                AchievementStateCoverage.UnlocksOnly));
    }

    [Fact]
    public void UnlocksOnlyPositiveState_PreservesConfirmedLowerBoundAndKnownTotal()
    {
        Assert.Equal(
            "10/28",
            GameDetailView.FormatLiveAchievementCount(
                unlocked: 10,
                total: 28,
                partialCatalogue: false,
                AchievementStateCoverage.UnlocksOnly));
    }

    [Fact]
    public void PartialCatalogue_KeepsConfirmedUnlockCountWithoutInventingTotal()
    {
        Assert.Equal(
            "4 desbloq.",
            GameDetailView.FormatLiveAchievementCount(
                unlocked: 4,
                total: 4,
                partialCatalogue: true,
                AchievementStateCoverage.UnlocksOnly));
    }

    private static LocalAchievement CreateModel(DateTimeOffset timestamp) =>
        new(
            "ACH_TEST",
            "Test achievement",
            "Description",
            Hidden: false,
            IsUnlocked: true,
            UnlockedAtUtc: timestamp,
            IconPath: null,
            LockedIconPath: null,
            Progress: null,
            MaxProgress: null);
}
