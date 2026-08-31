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

    [Fact]
    public void LocatorFindsOneLevelNestedPortableGseState()
    {
        var fixture = CreatePortableGseFixture("1478500");
        var statePath = fixture.WriteNestedState("account-1", """
            {
              "ACH_BIG_WALK": { "earned": true, "earned_time": 1788000000 }
            }
            """);

        var location = GseRuntimeAchievementStateLocator.TryLocate(fixture.ExecutablePath);

        Assert.NotNull(location);
        Assert.Equal("1478500", location.AppId);
        Assert.Equal(Path.GetFullPath(statePath), location.FilePath);
    }

    [Fact]
    public void LocatorUsesResolvedAppIdHintWhenLocalSteamAppIdFileIsMissing()
    {
        var fixture = CreatePortableGseFixture("1478500", writeSteamAppId: false);
        var statePath = fixture.WriteNestedState("account-1", """
            {
              "ACH_BIG_WALK": { "earned": true, "earned_time": 1788000000 }
            }
            """);

        var location = GseRuntimeAchievementStateLocator.TryLocate(
            fixture.ExecutablePath,
            appIdHint: "1478500");

        Assert.NotNull(location);
        Assert.Equal("1478500", location.AppId);
        Assert.Equal(Path.GetFullPath(statePath), location.FilePath);
    }

    [Fact]
    public void StatePathDiscoveryDoesNotDescendBeyondOneProfileDirectory()
    {
        var appDirectory = Path.Combine(_root, "GSE Saves", "1478500");
        var oneLevelDirectory = Path.Combine(appDirectory, "account-1");
        var oneLevelState = Path.Combine(oneLevelDirectory, "achievements.json");
        Directory.CreateDirectory(oneLevelDirectory);
        File.WriteAllText(oneLevelState, "{}");

        var deepDirectory = Path.Combine(appDirectory, "account-2", "nested");
        var deepState = Path.Combine(deepDirectory, "achievements.json");
        Directory.CreateDirectory(deepDirectory);
        File.WriteAllText(deepState, "{}");

        var states = GseAchievementStatePathLocator.FindExistingInAppDirectory(appDirectory);

        Assert.Contains(Path.GetFullPath(oneLevelState), states);
        Assert.DoesNotContain(Path.GetFullPath(deepState), states);
    }

    private PortableGseFixture CreatePortableGseFixture(string appId, bool writeSteamAppId = true)
    {
        var gameDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"), "game");
        Directory.CreateDirectory(gameDirectory);
        return new PortableGseFixture(gameDirectory, appId, writeSteamAppId);
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
        private readonly string _settingsDirectory;
        private readonly string _saveRoot;
        private readonly string _appId;

        public PortableGseFixture(string gameDirectory, string appId, bool writeSteamAppId)
        {
            _appId = appId;
            _settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
            _saveRoot = Path.Combine(gameDirectory, "portable-saves");
            Directory.CreateDirectory(_settingsDirectory);
            ExecutablePath = Path.Combine(gameDirectory, "game.exe");
            File.WriteAllBytes(ExecutablePath, Array.Empty<byte>());
            if (writeSteamAppId)
            {
                File.WriteAllText(Path.Combine(_settingsDirectory, "steam_appid.txt"), appId);
            }
            File.WriteAllText(
                Path.Combine(_settingsDirectory, "configs.user.ini"),
                "[user::saves]\nlocal_save_path=./portable-saves\nsaves_folder_name=GSE Saves\n");
        }

        public string ExecutablePath { get; }

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

        public string WriteNestedState(string profile, string json)
        {
            var directory = Path.Combine(_saveRoot, _appId, profile);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "achievements.json");
            File.WriteAllText(path, json);
            return path;
        }
    }
}
