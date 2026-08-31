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
        var fixture = CreatePortableGseFixture(appId);
        fixture.WriteDefinitions("""
            [
              { "name": "ACH_FIRST", "displayName": "First", "description": "First achievement" },
              { "name": "ACH_SECOND", "displayName": "Second", "description": "Second achievement" }
            ]
            """);
        fixture.WriteGseState("""
            {
              "ACH_FIRST": { "earned": true, "earned_time": 1787846400 }
            }
            """);

        var result = new AggregatingLocalAchievementProvider().TryReadDetailed(fixture.ExecutablePath);

        Assert.Equal(AchievementReadStatus.Success, result.Status);
        Assert.Equal(AchievementSourceHealth.Healthy, result.Health);
        Assert.Equal(AchievementStateCoverage.UnlocksOnly, result.StateCoverage);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Snapshot);
        Assert.True(result.Snapshot.IsCatalogueComplete);
        Assert.Equal(2, result.Snapshot.Achievements.Count);
        Assert.Equal(1, result.Snapshot.UnlockedCount);
    }

    [Fact]
    public void DetailedRead_ExplicitGoldbergIdentityIgnoresStaleRuneStateForSameAppId()
    {
        const string appId = "3946950";
        var fixture = CreatePortableGseFixture(appId);
        fixture.WriteDefinitions("""
            [
              { "name": "ACH_GSE", "displayName": "GSE", "description": "Current runtime achievement" },
              { "name": "ACH_RUNE", "displayName": "RUNE", "description": "Stale runtime achievement" }
            ]
            """);
        fixture.WriteGseState("""
            {
              "ACH_GSE": { "earned": true, "earned_time": 1787846400 }
            }
            """);
        fixture.WriteRuneState("""
            [ACH_RUNE]
            Achieved=1
            UnlockTime=1787846500
            """);

        var result = new AggregatingLocalAchievementProvider().TryReadDetailed(fixture.ExecutablePath);

        Assert.Equal(AchievementReadStatus.Success, result.Status);
        Assert.Equal(AchievementSourceHealth.Healthy, result.Health);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(1, result.Snapshot.UnlockedCount);
        Assert.True(Assert.Single(result.Snapshot.Achievements, item => item.ApiName == "ACH_GSE").IsUnlocked);
        Assert.False(Assert.Single(result.Snapshot.Achievements, item => item.ApiName == "ACH_RUNE").IsUnlocked);
    }

    [Fact]
    public void DetailedRead_UnknownRuntimeWithRuneAndCodexSameAppIdFailsAmbiguous()
    {
        const string appId = "1297900";
        var gameDirectory = Path.Combine(_root, "ambiguous-game");
        Directory.CreateDirectory(gameDirectory);
        var executable = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(gameDirectory, "steam_emu.ini"), $"AppId={appId}\n");

        var runeDirectory = Path.Combine(gameDirectory, "Steam", "RUNE", appId);
        Directory.CreateDirectory(runeDirectory);
        File.WriteAllText(
            Path.Combine(runeDirectory, "achievements.ini"),
            "[ACH_RUNE]\nAchieved=1\nUnlockTime=1787846400\n");

        var codexDirectory = Path.Combine(gameDirectory, "Steam", "CODEX", appId);
        Directory.CreateDirectory(codexDirectory);
        File.WriteAllText(
            Path.Combine(codexDirectory, "achievements.ini"),
            "[ACH_CODEX]\nAchieved=1\nUnlockTime=1787846500\n");

        var result = new AggregatingLocalAchievementProvider().TryReadDetailed(executable);

        Assert.Equal(AchievementReadStatus.Ambiguous, result.Status);
        Assert.Equal(AchievementSourceHealth.Ambiguous, result.Health);
        Assert.Equal(AchievementStateCoverage.Unknown, result.StateCoverage);
        Assert.Null(result.Snapshot);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AchievementReadStatus.Ambiguous, diagnostic.Status);
        Assert.Contains("Rune", diagnostic.Detail, StringComparison.Ordinal);
        Assert.Contains("Codex", diagnostic.Detail, StringComparison.Ordinal);
        Assert.Contains("No cross-family state was merged", diagnostic.Detail, StringComparison.Ordinal);
    }

    private PortableGseFixture CreatePortableGseFixture(string appId)
    {
        var gameDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"), "game");
        Directory.CreateDirectory(gameDirectory);
        return new PortableGseFixture(gameDirectory, appId);
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

    private sealed class PortableGseFixture
    {
        private readonly string _gameDirectory;
        private readonly string _settingsDirectory;
        private readonly string _saveRoot;
        private readonly string _appId;

        public PortableGseFixture(string gameDirectory, string appId)
        {
            _gameDirectory = gameDirectory;
            _appId = appId;
            _settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
            _saveRoot = Path.Combine(gameDirectory, "portable-saves");
            Directory.CreateDirectory(_settingsDirectory);
            ExecutablePath = Path.Combine(gameDirectory, "game.exe");
            File.WriteAllBytes(ExecutablePath, Array.Empty<byte>());
            File.WriteAllText(Path.Combine(_settingsDirectory, "steam_appid.txt"), appId);
            File.WriteAllText(
                Path.Combine(_settingsDirectory, "configs.user.ini"),
                "[user::saves]\nlocal_save_path=./portable-saves\nsaves_folder_name=GSE Saves\n");
        }

        public string ExecutablePath { get; }

        public void WriteDefinitions(string json) =>
            File.WriteAllText(Path.Combine(_settingsDirectory, "achievements.json"), json);

        public void WriteGseState(string json)
        {
            var directory = Path.Combine(_saveRoot, _appId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "achievements.json"), json);
        }

        public void WriteRuneState(string ini)
        {
            var directory = Path.Combine(_gameDirectory, "Steam", "RUNE", _appId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "achievements.ini"), ini);
        }
    }
}
