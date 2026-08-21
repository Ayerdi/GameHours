using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;
using GameHours.Windows.Discovery;

namespace GameHours.Windows.Tests;

public sealed class WindowsGameDetectionEvidenceTests
{
    [Theory]
    [InlineData("CrashReportClient.exe", ExecutableRole.CrashHandler)]
    [InlineData("UnityCrashHandler64.exe", ExecutableRole.CrashHandler)]
    [InlineData("EasyAntiCheat_EOS.exe", ExecutableRole.AntiCheat)]
    [InlineData("BEService_x64.exe", ExecutableRole.AntiCheat)]
    [InlineData("steamwebhelper.exe", ExecutableRole.Helper)]
    [InlineData("EpicGamesLauncher.exe", ExecutableRole.Launcher)]
    [InlineData("MyGameLauncher.exe", ExecutableRole.Launcher)]
    [InlineData("GameUpdater.exe", ExecutableRole.Updater)]
    [InlineData("G1R-Win64-Shipping.exe", ExecutableRole.PrimaryGame)]
    public void ClassifierRecognizesConservativeExecutableRoles(string fileName, ExecutableRole expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", fileName);

        var role = WindowsExecutableRoleClassifier.Classify(path);

        Assert.Equal(expected, role);
    }

    [Fact]
    public async Task ExactGameConfigStoreMatchCanResolveLooseGameWithoutLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"), "Loose Game");
        var path = Path.Combine(root, "LooseGame.exe");
        var collector = new WindowsProcessEvidenceCollector(
            new FakeGameConfigStore(path),
            inspectLiveProcess: false);
        var resolver = new WindowsGameResolver(Array.Empty<DiscoveredGame>(), collector);

        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(123, "LooseGame", path, null));

        Assert.NotNull(resolution.Game);
        Assert.Equal("windows_game_config_store", resolution.Method);
        Assert.True(resolution.Confidence >= 0.80);
        Assert.Equal(ExecutableRole.PrimaryGame, resolution.Role);
        Assert.Contains(
            resolution.DetectionEvidence,
            evidence => evidence.Kind == GameDetectionEvidenceKind.WindowsGameConfigStore);
    }

    [Fact]
    public async Task HelperRoleWinsOverGameConfigStoreMatch()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "EpicGamesLauncher.exe");
        var collector = new WindowsProcessEvidenceCollector(
            new FakeGameConfigStore(path),
            inspectLiveProcess: false);
        var resolver = new WindowsGameResolver(Array.Empty<DiscoveredGame>(), collector);

        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(124, "EpicGamesLauncher", path, null));

        Assert.Null(resolution.Game);
        Assert.True(resolution.IsHelperProcess);
        Assert.Equal(ExecutableRole.Launcher, resolution.Role);
        Assert.Equal("ignored_process_role", resolution.Method);
    }

    [Fact]
    public async Task UnknownExecutableInsideKnownInstallIsTrackableSecondaryProcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"), "KnownGame");
        var path = Path.Combine(root, "worker.exe");
        var installed = new DiscoveredGame(
            Guid.NewGuid(),
            "Known Game",
            GameDiscoverySource.Steam,
            "123",
            root,
            null,
            1.0);
        var collector = new WindowsProcessEvidenceCollector(
            new FakeGameConfigStore(),
            inspectLiveProcess: false);
        var resolver = new WindowsGameResolver(new[] { installed }, collector);

        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(125, "worker", path, null));

        Assert.Equal(installed.GameId, resolution.Game?.Id);
        Assert.Equal(ExecutableRole.SecondaryGame, resolution.Role);
        Assert.False(resolution.IsHelperProcess);
        Assert.Contains(
            resolution.DetectionEvidence,
            evidence => evidence.Kind == GameDetectionEvidenceKind.InstalledGamePath);
    }

    private sealed class FakeGameConfigStore : IWindowsGameConfigStore
    {
        private readonly HashSet<string> _paths;

        public FakeGameConfigStore(params string[] paths)
        {
            _paths = paths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public bool ContainsExecutable(string executablePath) =>
            _paths.Contains(Path.GetFullPath(executablePath));
    }
}
