using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class LocalAchievementSourceLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public LocalAchievementSourceLocatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Locate_FindsSteamSettingsDefinitionsAndGameDirectoryFormats()
    {
        var game = Path.Combine(_root, "ExampleGame");
        var bin = Path.Combine(game, "bin");
        Directory.CreateDirectory(bin);
        var executable = Path.Combine(bin, "example.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());

        var settings = Path.Combine(game, "steam_settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "steam_appid.txt"), "123456");
        File.WriteAllText(Path.Combine(settings, "achievements.json"), "[]");

        var userStats = Path.Combine(game, "SteamData");
        Directory.CreateDirectory(userStats);
        File.WriteAllText(Path.Combine(userStats, "user_stats.ini"), "[ACHIEVEMENTS]");

        var threeDm = Path.Combine(game, "3DMGAME", "player", "stats");
        Directory.CreateDirectory(threeDm);
        File.WriteAllText(Path.Combine(threeDm, "achievements.ini"), "[State]");

        var sources = new LocalAchievementSourceLocator().Locate(executable);

        Assert.Contains(sources, source =>
            source.Kind == LocalAchievementSourceKind.SteamSettingsDefinitions &&
            source.AppId == "123456");
        Assert.Contains(sources, source => source.Kind == LocalAchievementSourceKind.UserStats);
        Assert.Contains(sources, source => source.Kind == LocalAchievementSourceKind.ThreeDm);
    }

    [Fact]
    public void Locate_ReadsRuneAppIdFromSteamEmuIniAndFindsPortableSave()
    {
        var game = Path.Combine(_root, "Gothic1Remake");
        var binaries = Path.Combine(game, "Alkimia", "Binaries", "Win64");
        Directory.CreateDirectory(binaries);
        var executable = Path.Combine(binaries, "Alkimia-Win64-Shipping.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(binaries, "steam_emu.ini"), """
            [Settings]
            AppId=1297900
            UserName=RUNE
            """);

        var runeDirectory = Path.Combine(game, "Steam", "RUNE", "1297900");
        Directory.CreateDirectory(runeDirectory);
        var achievements = Path.Combine(runeDirectory, "achievements.ini");
        File.WriteAllText(achievements, """
            [SteamAchievements]
            Count=1
            [ACH_FIRST]
            Achieved=1
            UnlockTime=1700000000
            """);

        var sources = new LocalAchievementSourceLocator().Locate(executable);

        var source = Assert.Single(sources, item => item.Kind == LocalAchievementSourceKind.Rune);
        Assert.Equal("1297900", source.AppId);
        Assert.Equal(Path.GetFullPath(achievements), Path.GetFullPath(source.FilePath));
        Assert.Equal("game_directory", source.Scope);
    }

    [Fact]
    public void Locate_FindsPortableRuneTreeEvenWithoutEmulatorConfig()
    {
        var game = Path.Combine(_root, "PortableGame");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "portable.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());

        var runeDirectory = Path.Combine(game, "Steam", "RUNE", "777777");
        Directory.CreateDirectory(runeDirectory);
        var achievements = Path.Combine(runeDirectory, "achievements.ini");
        File.WriteAllText(achievements, "[ACH_ONE]\nAchieved=1");

        var sources = new LocalAchievementSourceLocator().Locate(executable);

        Assert.Contains(sources, source =>
            source.Kind == LocalAchievementSourceKind.Rune &&
            source.AppId == "777777" &&
            Path.GetFullPath(source.FilePath) == Path.GetFullPath(achievements));
    }

    [Fact]
    public void Locate_ReturnsOnlyFilesThatActuallyExist()
    {
        var game = Path.Combine(_root, "EmptyGame");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "empty.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());

        var sources = new LocalAchievementSourceLocator().Locate(executable, "999999");

        Assert.All(sources, source => Assert.True(File.Exists(source.FilePath)));
        Assert.DoesNotContain(sources, source => source.Scope == "game_directory");
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
