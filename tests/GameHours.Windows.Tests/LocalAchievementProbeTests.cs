using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class LocalAchievementProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "gamehours-achievement-probe-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProbeDoesNotScanSiblingGamesWhenExecutableDirectoryHasSteamSettings()
    {
        var gameDirectory = Path.Combine(_root, "Click.the.Button.v1.0.ZeiGames.com");
        var settingsDirectory = Path.Combine(gameDirectory, "steam_settings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(Path.Combine(settingsDirectory, "steam_appid.txt"), "3946950");

        var executablePath = Path.Combine(gameDirectory, "Click the Button.exe");
        File.WriteAllBytes(executablePath, Array.Empty<byte>());

        var siblingDirectory = Path.Combine(_root, "Barony");
        var siblingSettings = Path.Combine(siblingDirectory, "steam_settings");
        Directory.CreateDirectory(siblingSettings);
        File.WriteAllText(Path.Combine(siblingSettings, "steam_appid.txt"), "371970");
        File.WriteAllText(Path.Combine(siblingSettings, "achievements.json"), "[]");

        var result = new LocalAchievementProbe().Probe("Click the Button", executablePath);

        Assert.Equal(Path.GetFullPath(gameDirectory), result.GameRoot);
        Assert.Equal("3946950", result.SteamAppId);
        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Path.StartsWith(siblingDirectory, StringComparison.OrdinalIgnoreCase));
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
