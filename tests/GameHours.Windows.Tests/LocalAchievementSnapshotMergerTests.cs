using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class LocalAchievementSnapshotMergerTests
{
    [Fact]
    public void MergeCatalogueWithStates_PreservesCatalogueAndAppliesExternalUnlocks()
    {
        var catalogue = Snapshot(
            "catalogue",
            complete: true,
            statePath: null,
            Achievement("ACH_ONE", "First achievement"),
            Achievement("ACH_TWO", "Second achievement"));
        var unlockedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var codexState = Snapshot(
            "CODEX local · estado parcial",
            complete: false,
            statePath: @"C:\state\achievements.ini",
            Achievement("ACH_TWO", "ACH_TWO", unlocked: true, unlockedAt: unlockedAt));

        var result = LocalAchievementSnapshotMerger.MergeCatalogueWithStates(
            catalogue,
            new[] { codexState });

        Assert.True(result.IsCatalogueComplete);
        Assert.Equal(2, result.Achievements.Count);
        Assert.Equal(1, result.UnlockedCount);
        Assert.Equal(@"C:\state\achievements.ini", result.StatePath);
        Assert.Contains("CODEX local", result.Source, StringComparison.OrdinalIgnoreCase);

        var unlocked = Assert.Single(result.Achievements, item => item.ApiName == "ACH_TWO");
        Assert.Equal("Second achievement", unlocked.DisplayName);
        Assert.True(unlocked.IsUnlocked);
        Assert.Equal(unlockedAt, unlocked.UnlockedAtUtc);
    }

    [Fact]
    public void MergeCatalogueWithStates_IgnoresUnknownStateIdsWhenCatalogueIsComplete()
    {
        var catalogue = Snapshot(
            "catalogue",
            complete: true,
            statePath: null,
            Achievement("ACH_KNOWN", "Known"));
        var state = Snapshot(
            "local state",
            complete: false,
            statePath: "state.ini",
            Achievement("ACH_UNKNOWN", "ACH_UNKNOWN", unlocked: true));

        var result = LocalAchievementSnapshotMerger.MergeCatalogueWithStates(catalogue, new[] { state });

        Assert.Single(result.Achievements);
        Assert.Equal(0, result.UnlockedCount);
        Assert.DoesNotContain(result.Achievements, item => item.ApiName == "ACH_UNKNOWN");
    }

    [Fact]
    public void MergePartialStates_UnionsSourcesAndUsesEarliestUnlockTimestamp()
    {
        var later = DateTimeOffset.FromUnixTimeSeconds(1_700_001_000);
        var earlier = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var codex = Snapshot(
            "CODEX local · estado parcial",
            complete: false,
            statePath: "codex.ini",
            Achievement("ACH_SHARED", "ACH_SHARED", unlocked: true, unlockedAt: later),
            Achievement("ACH_CODEX", "ACH_CODEX", unlocked: true));
        var steam = Snapshot(
            "Steam local cache · estado parcial",
            complete: false,
            statePath: "steam.json",
            Achievement("ACH_SHARED", "ACH_SHARED", unlocked: true, unlockedAt: earlier),
            Achievement("ACH_STEAM", "ACH_STEAM", unlocked: true));

        var result = LocalAchievementSnapshotMerger.MergePartialStates(new[] { codex, steam });

        Assert.NotNull(result);
        Assert.False(result.IsCatalogueComplete);
        Assert.Equal(3, result.UnlockedCount);
        Assert.Contains("CODEX local", result.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Steam local cache", result.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(earlier, Assert.Single(result.Achievements, item => item.ApiName == "ACH_SHARED").UnlockedAtUtc);
    }

    [Fact]
    public void MergePartialStates_ReturnsNullWhenNoUnlockedStateExists()
    {
        var state = Snapshot(
            "empty",
            complete: false,
            statePath: "empty.ini",
            Achievement("ACH_LOCKED", "ACH_LOCKED"));

        Assert.Null(LocalAchievementSnapshotMerger.MergePartialStates(new[] { state }));
    }

    private static LocalAchievementSnapshot Snapshot(
        string source,
        bool complete,
        string? statePath,
        params LocalAchievement[] achievements) =>
        new(source, "123456", "catalogue.json", statePath, achievements)
        {
            IsCatalogueComplete = complete
        };

    private static LocalAchievement Achievement(
        string apiName,
        string displayName,
        bool unlocked = false,
        DateTimeOffset? unlockedAt = null) =>
        new(
            apiName,
            displayName,
            $"Description for {apiName}",
            Hidden: false,
            IsUnlocked: unlocked,
            UnlockedAtUtc: unlockedAt,
            IconPath: null,
            LockedIconPath: null,
            Progress: null,
            MaxProgress: null);
}
