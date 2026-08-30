using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class SteamCompatibleAppIdResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public SteamCompatibleAppIdResolverTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TryResolve_FindsSteamEmuIniInDeepUnrealSteamworksRuntime()
    {
        var game = Path.Combine(_root, "Gothic 1 Remake");
        var executableDirectory = Path.Combine(game, "G1R", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "G1R-Win64-Shipping.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());

        var steamRuntime = Path.Combine(
            game,
            "Engine",
            "Binaries",
            "ThirdParty",
            "Steamworks",
            "Steamv157",
            "Win64");
        Directory.CreateDirectory(steamRuntime);
        File.WriteAllBytes(Path.Combine(steamRuntime, "steam_api64.dll"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(steamRuntime, "steam_emu.ini"), """
            [Settings]
            AppId=1297900
            UserName=RUNE
            """);

        var appId = new SteamCompatibleAppIdResolver().TryResolve(executable);

        Assert.Equal("1297900", appId);
    }

    [Fact]
    public void TryResolve_PrefersSteamEmuConfigBesideSteamApiOverUnrelatedDeepMarker()
    {
        var game = Path.Combine(_root, "Example Game");
        var executableDirectory = Path.Combine(game, "Game", "Binaries", "Win64");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "Game-Win64-Shipping.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(game, "Engine"));

        var unrelated = Path.Combine(game, "Mods", "SomeTool");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "steam_emu.ini"), "AppId=111111");

        var runtime = Path.Combine(game, "Engine", "Binaries", "ThirdParty", "Steamworks", "Win64");
        Directory.CreateDirectory(runtime);
        File.WriteAllBytes(Path.Combine(runtime, "steam_api64.dll"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(runtime, "steam_emu.ini"), "AppId=222222");

        var appId = new SteamCompatibleAppIdResolver().TryResolve(executable);

        Assert.Equal("222222", appId);
    }

    [Fact]
    public void TryResolve_UsesOnlineFixRealAppId()
    {
        var game = Path.Combine(_root, "OnlineFix Game");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "game.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(game, "steam_appid.txt"), "480");
        File.WriteAllText(Path.Combine(game, "OnlineFix.ini"), """
            [Main]
            RealAppId=1478500
            FakeAppId=480
            """);

        var appId = new SteamCompatibleAppIdResolver().TryResolve(executable);

        Assert.Equal("1478500", appId);
    }

    [Fact]
    public void TryResolve_ConflictingStrongMarkersReturnNull()
    {
        var game = Path.Combine(_root, "Conflicting Game");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "game.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(game, "OnlineFix.ini"), """
            [Main]
            RealAppId=1478500
            FakeAppId=480
            """);
        File.WriteAllText(Path.Combine(game, "steam_emu.ini"), """
            [Settings]
            AppId=999999
            """);

        var appId = new SteamCompatibleAppIdResolver().TryResolve(executable);

        Assert.Null(appId);
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
