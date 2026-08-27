using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class GseRuntimeAchievementStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public GseRuntimeAchievementStateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void AggregatorReadsPortableGseStateWithoutLocalCatalogue()
    {
        var fixture = CreatePortableGseFixture("3946950");
        var statePath = fixture.WriteState("""
            {
              "ACH_FIRST": { "earned": true, "earned_time": 1787846400 },
              "ACH_LOCKED": { "earned": false, "earned_time": 0 }
            }
            """);

        var snapshot = new AggregatingLocalAchievementProvider().TryRead(fixture.ExecutablePath);

        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsCatalogueComplete);
        Assert.Equal("3946950", snapshot.AppId);
        Assert.Equal(Path.GetFullPath(statePath), snapshot.StatePath);
        var unlocked = Assert.Single(snapshot.Achievements);
        Assert.Equal("ACH_FIRST", unlocked.ApiName);
        Assert.True(unlocked.IsUnlocked);
        Assert.NotNull(unlocked.UnlockedAtUtc);
    }

    [Fact]
    public void AggregatorMergesPortableGseStateIntoLocalCatalogue()
    {
        var fixture = CreatePortableGseFixture("3946950");
        fixture.WriteDefinitions("""
            [
              { "name": "ACH_FIRST", "displayName": "First", "description": "First achievement" },
              { "name": "ACH_SECOND", "displayName": "Second", "description": "Second achievement" }
            ]
            """);
        var statePath = fixture.WriteState("""
            {
              "ACH_FIRST": { "earned": true, "earned_time": 1787846400 }
            }
            """);

        var snapshot = new AggregatingLocalAchievementProvider().TryRead(fixture.ExecutablePath);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsCatalogueComplete);
        Assert.Equal(2, snapshot.Achievements.Count);
        Assert.Equal(1, snapshot.UnlockedCount);
        Assert.Equal(Path.GetFullPath(statePath), snapshot.StatePath);
        Assert.True(Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_FIRST").IsUnlocked);
        Assert.False(Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_SECOND").IsUnlocked);
    }

    [Fact]
    public void LocatorResolvesLocalSavePathRelativeToSteamApiDirectory()
    {
        var fixture = CreatePortableGseFixture("3946950");
        var statePath = fixture.WriteState("{}");

        var location = GseRuntimeAchievementStateLocator.TryLocate(fixture.ExecutablePath);

        Assert.NotNull(location);
        Assert.Equal("3946950", location.AppId);
        Assert.Equal(Path.GetFullPath(statePath), location.FilePath);
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
            _settingsDirectory = Path.Combine(_gameDirectory, "steam_settings");
            _saveRoot = Path.Combine(_gameDirectory, "portable-saves");
            Directory.CreateDirectory(_settingsDirectory);
            File.WriteAllBytes(ExecutablePath, Array.Empty<byte>());
            File.WriteAllText(Path.Combine(_settingsDirectory, "steam_appid.txt"), appId);
            File.WriteAllText(
                Path.Combine(_settingsDirectory, "configs.user.ini"),
                "[user::saves]\nlocal_save_path=./portable-saves\nsaves_folder_name=GSE Saves\n");
        }

        public string ExecutablePath => Path.Combine(_gameDirectory, "game.exe");

        public void WriteDefinitions(string json) =>
            File.WriteAllText(Path.Combine(_settingsDirectory, "achievements.json"), json);

        public string WriteState(string json)
        {
            var directory = Path.Combine(_saveRoot, _appId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "achievements.json");
            File.WriteAllText(path, json);
            return path;
        }
    }
}
