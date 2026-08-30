using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class AggregatingLocalAchievementProviderDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public AggregatingLocalAchievementProviderDiagnosticsTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DetailedRead_DoesNotTreatHandledSteamSettingsCatalogueAsUnsupportedState()
    {
        const string appId = "3946950";
        var gameDirectory = Path.Combine(_root, "game");
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        var saveRoot = Path.Combine(gameDirectory, "portable-saves");
        var stateDirectory = Path.Combine(saveRoot, appId);
        Directory.CreateDirectory(settingsDirectory);
        Directory.CreateDirectory(stateDirectory);

        var executable = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), appId);
        File.WriteAllText(
            Path.Combine(settingsDirectory, "configs.user.ini"),
            "[user::saves]\nlocal_save_path=./portable-saves\nsaves_folder_name=GSE Saves\n");
        File.WriteAllText(
            Path.Combine(settingsDirectory, "achievements.json"),
            """
            [
              { "name": "ACH_FIRST", "displayName": "First", "description": "First achievement" },
              { "name": "ACH_SECOND", "displayName": "Second", "description": "Second achievement" }
            ]
            """);
        File.WriteAllText(
            Path.Combine(stateDirectory, "achievements.json"),
            """
            {
              "ACH_FIRST": { "earned": true, "earned_time": 1787846400 }
            }
            """);

        var result = new AggregatingLocalAchievementProvider().TryReadDetailed(executable);

        Assert.Equal(AchievementReadStatus.Success, result.Status);
        Assert.Equal(AchievementSourceHealth.Healthy, result.Health);
        Assert.Equal(AchievementStateCoverage.UnlocksOnly, result.StateCoverage);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot.IsCatalogueComplete);
        Assert.Equal(2, result.Snapshot.Achievements.Count);
        Assert.Equal(1, result.Snapshot.UnlockedCount);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
