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

        var resolution = CreateResolver().TryResolveDetailed(executable);

        Assert.NotNull(resolution);
        Assert.Equal("1297900", resolution.AppId);
        Assert.Equal("steam_emu.ini AppId", resolution.EvidenceSource);
        Assert.Equal(SteamAppIdConfidence.High, resolution.Confidence);
        Assert.False(resolution.FromPersistentCache);
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

        var appId = CreateResolver().TryResolve(executable);

        Assert.Equal("222222", appId);
    }

    [Fact]
    public void TryResolve_StrongDeepRuntimeBeatsGenericAncestorOverride()
    {
        var game = Path.Combine(_root, "Nested Runtime Game");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "game.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(game, "steam_appid.txt"), "480");
        Directory.CreateDirectory(Path.Combine(game, "Engine"));

        var runtime = Path.Combine(game, "Engine", "Binaries", "ThirdParty", "Steamworks", "Win64");
        Directory.CreateDirectory(runtime);
        File.WriteAllBytes(Path.Combine(runtime, "steam_api64.dll"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(runtime, "steam_emu.ini"), "AppId=777777");

        var appId = CreateResolver().TryResolve(executable);

        Assert.Equal("777777", appId);
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

        var resolution = CreateResolver().TryResolveDetailed(executable);

        Assert.NotNull(resolution);
        Assert.Equal("1478500", resolution.AppId);
        Assert.Equal("OnlineFix RealAppId", resolution.EvidenceSource);
        Assert.Equal(SteamAppIdConfidence.High, resolution.Confidence);
    }

    [Theory]
    [InlineData("CPY.ini", "[Settings]\nAppID=1035208\nPlayerName=CPY\n", "1035208", "CPY AppID")]
    [InlineData("SmartSteamEmu.ini", "[SmartSteamEmu]\nAppId = 221380\n", "221380", "SmartSteamEmu AppId")]
    [InlineData("tenoke.ini", "[TENOKE]\nid = 3764200 # Example\n", "3764200", "TENOKE id")]
    [InlineData("ColdClientLoader.ini", "[SteamClient]\nExe=game.exe\nAppId=813780\n", "813780", "ColdClientLoader AppId")]
    public void TryResolve_ReadsExplicitRuntimeIdentityFormats(
        string fileName,
        string content,
        string expectedAppId,
        string expectedSource)
    {
        var game = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "game.exe");
        File.WriteAllBytes(executable, Array.Empty<byte>());
        File.WriteAllText(Path.Combine(game, fileName), content);

        var resolution = CreateResolver().TryResolveDetailed(executable);

        Assert.NotNull(resolution);
        Assert.Equal(expectedAppId, resolution.AppId);
        Assert.Equal(expectedSource, resolution.EvidenceSource);
        Assert.Equal(SteamAppIdConfidence.High, resolution.Confidence);
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

        var appId = CreateResolver().TryResolve(executable);

        Assert.Null(appId);
    }

    [Fact]
    public void TryResolve_PersistentVerifiedIdentitySurvivesMissingMarkerForSameExecutable()
    {
        var game = Path.Combine(_root, "Cached Game");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "game.exe");
        File.WriteAllBytes(executable, new byte[] { 1, 2, 3, 4 });
        var marker = Path.Combine(game, "OnlineFix.ini");
        File.WriteAllText(marker, "[Main]\nRealAppId=1478500\nFakeAppId=480\n");
        var cache = Path.Combine(_root, "identity-cache.json");

        var first = new SteamCompatibleAppIdResolver(cache).TryResolveDetailed(executable);
        Assert.NotNull(first);
        Assert.False(first.FromPersistentCache);

        File.Delete(marker);
        var second = new SteamCompatibleAppIdResolver(cache).TryResolveDetailed(executable);

        Assert.NotNull(second);
        Assert.Equal("1478500", second.AppId);
        Assert.True(second.FromPersistentCache);
    }

    [Fact]
    public void TryResolve_PersistentIdentityIsRejectedWhenExecutableChanges()
    {
        var game = Path.Combine(_root, "Changed Cached Game");
        Directory.CreateDirectory(game);
        var executable = Path.Combine(game, "game.exe");
        File.WriteAllBytes(executable, new byte[] { 1, 2, 3, 4 });
        var marker = Path.Combine(game, "OnlineFix.ini");
        File.WriteAllText(marker, "[Main]\nRealAppId=1478500\nFakeAppId=480\n");
        var cache = Path.Combine(_root, "changed-identity-cache.json");

        Assert.Equal("1478500", new SteamCompatibleAppIdResolver(cache).TryResolve(executable));
        File.Delete(marker);
        File.WriteAllBytes(executable, new byte[] { 1, 2, 3, 4, 5 });

        var appId = new SteamCompatibleAppIdResolver(cache).TryResolve(executable);

        Assert.Null(appId);
    }

    private static SteamCompatibleAppIdResolver CreateResolver() =>
        new(persistentCachePath: null);

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
