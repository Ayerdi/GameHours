using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class LocalAchievementSupportInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "gamehours-achievement-support-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void InspectExplainsGseInstallationWithoutCatalogueOrRuntimeState()
    {
        var gameDirectory = CreateGameDirectory();
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), "3946950");
        File.WriteAllText(
            Path.Combine(settingsDirectory, "configs.user.ini"),
            "[user::saves]\nlocal_save_path=./path/relative/to/dll\nsaves_folder_name=GSE Saves\n");

        var executablePath = Path.Combine(gameDirectory, "Click the Button.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        var hint = new LocalAchievementSupportInspector().Inspect(executablePath);

        Assert.NotNull(hint);
        Assert.Contains("GSE/Goldberg", hint!.SourceText, StringComparison.Ordinal);
        Assert.Contains("no incluye un catálogo", hint.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectRecognizesGoldbergSteamInterfacesMarker()
    {
        var gameDirectory = CreateGameDirectory();
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), "3946950");
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_interfaces.txt"), "SteamUserStats012");

        var executablePath = Path.Combine(gameDirectory, "Game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        var hint = new LocalAchievementSupportInspector().Inspect(executablePath);

        Assert.NotNull(hint);
        Assert.Contains("GSE/Goldberg", hint!.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectDoesNotReportIncompleteSupportWhenGseCatalogueExists()
    {
        var gameDirectory = CreateGameDirectory();
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "configs.user.ini"), "[user::saves]\n");
        File.WriteAllText(Path.Combine(settingsDirectory, "achievements.json"), "[]");

        var executablePath = Path.Combine(gameDirectory, "Game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        Assert.Null(new LocalAchievementSupportInspector().Inspect(executablePath));
    }

    [Fact]
    public void InspectDoesNotTreatGenericSteamSettingsAsGse()
    {
        var gameDirectory = CreateGameDirectory();
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), "3946950");

        var executablePath = Path.Combine(gameDirectory, "Game.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        Assert.Null(new LocalAchievementSupportInspector().Inspect(executablePath));
    }

    private string CreateGameDirectory()
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
