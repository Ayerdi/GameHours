using System.Text.Json;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class GseAchievementCatalogueProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public GseAchievementCatalogueProvisionerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ClickLikeLayoutGetsMinimalCatalogueWithoutInventingUnlocks()
    {
        var gameDirectory = CreateGameDirectory("Click");
        var executablePath = Path.Combine(gameDirectory, "ClickTheButton.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), "3946950");
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_interfaces.txt"), "SteamUserStats012");
        File.WriteAllText(
            Path.Combine(settingsDirectory, "configs.user.ini"),
            "[user::saves]\nlocal_save_path=./path/relative/to/dll\nsaves_folder_name=GSE Saves\n");

        var provisioner = CreateProvisioner("ACH_SECOND", "ACH_FIRST", "ACH_FIRST");

        var result = await provisioner.TryProvisionAsync(executablePath);

        Assert.Equal(GseAchievementCatalogueProvisioningStatus.Created, result.Status);
        Assert.Equal("3946950", result.AppId);
        Assert.Equal(2, result.AchievementCount);
        Assert.NotNull(result.DefinitionPath);
        Assert.True(File.Exists(result.DefinitionPath));

        using var document = JsonDocument.Parse(File.ReadAllText(result.DefinitionPath));
        var entries = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("ACH_FIRST", entries[0].GetProperty("name").GetString());
        Assert.Equal("ACH_FIRST", entries[0].GetProperty("displayName").GetString());
        Assert.Equal(string.Empty, entries[0].GetProperty("description").GetString());
        Assert.Equal("0", entries[0].GetProperty("hidden").GetString());
        Assert.False(entries[0].TryGetProperty("icon", out _));
        Assert.False(entries[0].TryGetProperty("icongray", out _));

        var snapshot = new GseAchievementReader().TryRead(executablePath);
        Assert.NotNull(snapshot);
        Assert.Equal("3946950", snapshot.AppId);
        Assert.Equal(2, snapshot.Achievements.Count);
        Assert.Equal(0, snapshot.UnlockedCount);
        Assert.Null(snapshot.StatePath);
    }

    [Fact]
    public async Task ExistingPortableRuntimeStateIsPreservedWhenMissingCatalogueIsProvisioned()
    {
        const string appId = "3456";
        const string runtimeJson = "{\n  \"ACH_EXISTING\": { \"earned\": true, \"earned_time\": 1787846400 }\n}";

        var gameDirectory = CreateGameDirectory("PartialState");
        var executablePath = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), appId);
        File.WriteAllText(
            Path.Combine(settingsDirectory, "configs.user.ini"),
            "[user::saves]\nlocal_save_path=./portable-saves\n");

        var stateDirectory = Path.Combine(gameDirectory, "portable-saves", appId);
        Directory.CreateDirectory(stateDirectory);
        var statePath = Path.Combine(stateDirectory, "achievements.json");
        File.WriteAllText(statePath, runtimeJson);

        var before = new AggregatingLocalAchievementProvider().TryRead(executablePath);
        Assert.NotNull(before);
        Assert.False(before.IsCatalogueComplete);
        Assert.Equal(1, before.UnlockedCount);
        Assert.Equal(Path.GetFullPath(statePath), before.StatePath);

        var result = await CreateProvisioner("ACH_EXISTING", "ACH_LOCKED").TryProvisionAsync(executablePath);

        Assert.Equal(GseAchievementCatalogueProvisioningStatus.Created, result.Status);
        Assert.Equal(runtimeJson, File.ReadAllText(statePath));

        var after = new AggregatingLocalAchievementProvider().TryRead(executablePath);
        Assert.NotNull(after);
        Assert.True(after.IsCatalogueComplete);
        Assert.Equal(2, after.Achievements.Count);
        Assert.Equal(1, after.UnlockedCount);
        Assert.Equal(Path.GetFullPath(statePath), after.StatePath);
        Assert.True(Assert.Single(after.Achievements, item => item.ApiName == "ACH_EXISTING").IsUnlocked);
        Assert.False(Assert.Single(after.Achievements, item => item.ApiName == "ACH_LOCKED").IsUnlocked);
    }

    [Fact]
    public async Task ExistingCatalogueIsNeverOverwrittenOrFetchedAgain()
    {
        var gameDirectory = CreateGameDirectory("Existing");
        var executablePath = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());
        var settingsDirectory = CreateModernGseSettings(gameDirectory, "1234");
        var definitionPath = Path.Combine(settingsDirectory, "achievements.json");
        const string original = "[{\"name\":\"KEEP_ME\"}]";
        File.WriteAllText(definitionPath, original);

        var fetchCount = 0;
        var provisioner = new GseAchievementCatalogueProvisioner((_, _) =>
        {
            fetchCount++;
            return Task.FromResult<IReadOnlyList<string>?>(new[] { "SHOULD_NOT_BE_USED" });
        });

        var result = await provisioner.TryProvisionAsync(executablePath);

        Assert.Equal(GseAchievementCatalogueProvisioningStatus.AlreadyPresent, result.Status);
        Assert.Equal(0, fetchCount);
        Assert.Equal(original, File.ReadAllText(definitionPath));
    }

    [Fact]
    public async Task GenericSteamSettingsDirectoryIsNotModified()
    {
        var gameDirectory = CreateGameDirectory("Generic");
        var executablePath = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);

        var fetchCount = 0;
        var provisioner = new GseAchievementCatalogueProvisioner((_, _) =>
        {
            fetchCount++;
            return Task.FromResult<IReadOnlyList<string>?>(new[] { "ACH" });
        });

        var result = await provisioner.TryProvisionAsync(executablePath);

        Assert.Equal(GseAchievementCatalogueProvisioningStatus.NotApplicable, result.Status);
        Assert.Equal(0, fetchCount);
        Assert.False(File.Exists(Path.Combine(settingsDirectory, "achievements.json")));
    }

    [Fact]
    public async Task NestedColdClientSettingsAreDiscoveredAndProvisioned()
    {
        var gameDirectory = CreateGameDirectory("ColdClient");
        var executablePath = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        var settingsDirectory = Path.Combine(gameDirectory, "coldclient", "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), "5678");
        File.WriteAllText(Path.Combine(settingsDirectory, "configs.main.ini"), "[main::general]\n");

        var result = await CreateProvisioner("ACH_ONE").TryProvisionAsync(executablePath);

        Assert.Equal(GseAchievementCatalogueProvisioningStatus.Created, result.Status);
        Assert.Equal(Path.GetFullPath(Path.Combine(settingsDirectory, "achievements.json")), Path.GetFullPath(result.DefinitionPath!));
        Assert.NotNull(new GseAchievementReader().TryRead(executablePath));
    }

    [Fact]
    public async Task DiscoveryDoesNotCrossIntoSiblingGame()
    {
        var targetDirectory = CreateGameDirectory(Path.Combine("Library", "Target"));
        var executablePath = Path.Combine(targetDirectory, "target.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        var siblingDirectory = CreateGameDirectory(Path.Combine("Library", "Sibling"));
        var siblingSettings = CreateModernGseSettings(siblingDirectory, "9999");

        var fetchCount = 0;
        var provisioner = new GseAchievementCatalogueProvisioner((_, _) =>
        {
            fetchCount++;
            return Task.FromResult<IReadOnlyList<string>?>(new[] { "ACH" });
        });

        var result = await provisioner.TryProvisionAsync(executablePath);

        Assert.Equal(GseAchievementCatalogueProvisioningStatus.NotApplicable, result.Status);
        Assert.Equal(0, fetchCount);
        Assert.False(File.Exists(Path.Combine(siblingSettings, "achievements.json")));
    }

    [Fact]
    public async Task EmptyRemoteCatalogueDoesNotCreateAnEmptyDefinitionsFile()
    {
        var gameDirectory = CreateGameDirectory("Empty");
        var executablePath = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());
        var settingsDirectory = CreateModernGseSettings(gameDirectory, "2468");

        var result = await CreateProvisioner().TryProvisionAsync(executablePath);

        Assert.Equal(GseAchievementCatalogueProvisioningStatus.CatalogueUnavailable, result.Status);
        Assert.False(File.Exists(Path.Combine(settingsDirectory, "achievements.json")));
    }

    private GseAchievementCatalogueProvisioner CreateProvisioner(params string[] names) =>
        new((_, _) => Task.FromResult<IReadOnlyList<string>?>(names));

    private string CreateGameDirectory(string relativePath)
    {
        var directory = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateModernGseSettings(string gameDirectory, string appId)
    {
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), appId);
        File.WriteAllText(Path.Combine(settingsDirectory, "configs.user.ini"), "[user::saves]\nsaves_folder_name=GSE Saves\n");
        return settingsDirectory;
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
