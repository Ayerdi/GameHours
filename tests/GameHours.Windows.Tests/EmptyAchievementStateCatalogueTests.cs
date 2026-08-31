using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class EmptyAchievementStateCatalogueTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public EmptyAchievementStateCatalogueTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void RuneCountZero_RemainsAValidEmptyUnlockOnlyState()
    {
        var statePath = Path.Combine(_root, "achievements.ini");
        File.WriteAllText(statePath, """
            [SteamAchievements]
            Count=0
            """);
        var candidate = new LocalAchievementSourceCandidate(
            LocalAchievementSourceKind.Rune,
            statePath,
            "1297900",
            "public_documents");

        var result = new PartialAchievementStateReader().TryReadDetailed(candidate);

        Assert.True(result.IsSuccess);
        Assert.Equal(AchievementReadStatus.Success, result.Status);
        Assert.Equal(AchievementSourceHealth.Healthy, result.Health);
        Assert.Equal(AchievementStateCoverage.UnlocksOnly, result.StateCoverage);
        Assert.Empty(result.Diagnostics);
        var state = Assert.IsType<LocalAchievementSnapshot>(result.Snapshot);
        Assert.Equal("1297900", state.AppId);
        Assert.Empty(state.Achievements);
        Assert.Equal(statePath, state.StatePath);
    }

    [Fact]
    public void MergeCatalogueWithStates_PreservesEmptyRuneStateAsAuthoritativeBaseline()
    {
        var catalogue = new LocalAchievementSnapshot(
            "Catálogo Steam en caché",
            "1297900",
            "steam-cache.json",
            StatePath: null,
            new[]
            {
                new LocalAchievement(
                    "ACH_FIRST",
                    "First",
                    "Description",
                    Hidden: false,
                    IsUnlocked: false,
                    UnlockedAtUtc: null,
                    IconPath: null,
                    LockedIconPath: null,
                    Progress: null,
                    MaxProgress: null)
            });
        var rune = new LocalAchievementSnapshot(
            "RUNE local · estado parcial",
            "1297900",
            "rune.ini",
            "rune.ini",
            Array.Empty<LocalAchievement>())
        {
            IsCatalogueComplete = false
        };

        var merged = LocalAchievementSnapshotMerger.MergeCatalogueWithStates(catalogue, new[] { rune });

        Assert.True(merged.IsCatalogueComplete);
        Assert.Equal(0, merged.UnlockedCount);
        Assert.Equal("rune.ini", merged.StatePath);
        Assert.Contains("RUNE local", merged.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Single(merged.Achievements);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
