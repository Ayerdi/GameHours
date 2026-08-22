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
    [InlineData("ConfigTool.exe", ExecutableRole.Helper)]
    [InlineData("Benchmark.exe", ExecutableRole.Helper)]
    [InlineData("setup.exe", ExecutableRole.Updater)]
    [InlineData("G1R-Win64-Shipping.exe", ExecutableRole.PrimaryGame)]
    public void ClassifierRecognizesConservativeExecutableRoles(string fileName, ExecutableRole expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", fileName);
        Assert.Equal(expected, WindowsExecutableRoleClassifier.Classify(path));
    }

    [Fact]
    public async Task ExactGameConfigStoreMatchCanResolveLooseGameWithoutLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"), "Loose Game");
        var path = Path.Combine(root, "LooseGame.exe");
        var collector = new WindowsProcessEvidenceCollector(new FakeGameConfigStore(path), inspectLiveProcess: false);
        var resolver = new WindowsGameResolver(Array.Empty<DiscoveredGame>(), collector);
        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(123, "LooseGame", path, null));
        Assert.NotNull(resolution.Game);
        Assert.Equal("windows_game_config_store", resolution.Method);
        Assert.True(resolution.Confidence >= 0.80);
    }

    [Fact]
    public async Task HelperRoleWinsOverGameConfigStoreMatch()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "EpicGamesLauncher.exe");
        var collector = new WindowsProcessEvidenceCollector(new FakeGameConfigStore(path), inspectLiveProcess: false);
        var resolver = new WindowsGameResolver(Array.Empty<DiscoveredGame>(), collector);
        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(124, "EpicGamesLauncher", path, null));
        Assert.Null(resolution.Game);
        Assert.True(resolution.IsHelperProcess);
        Assert.Equal(ExecutableRole.Launcher, resolution.Role);
    }

    [Fact]
    public async Task UnknownExecutableInsideKnownInstallNeedsRuntimeEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"), "KnownGame");
        var path = Path.Combine(root, "worker.exe");
        var installed = new DiscoveredGame(Guid.NewGuid(), "Known Game", GameDiscoverySource.Steam, "123", root, null, 1.0);
        var resolver = new WindowsGameResolver(new[] { installed }, new WindowsProcessEvidenceCollector(new FakeGameConfigStore(), inspectLiveProcess: false));
        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(125, "worker", path, null));
        Assert.Equal(installed.GameId, resolution.Game?.Id);
        Assert.Equal("installed_path_candidate", resolution.Method);
        Assert.True(resolution.Confidence < 0.80);
    }

    [Fact]
    public void SharedHistoryRecoversMappedParentIdentityWithoutReobservingItInResolver()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        var launcherPath = Path.Combine(root, "Launcher.exe");
        var childPath = Path.Combine(root, "RealGame.exe");
        var now = new MutableClock(new DateTimeOffset(2026, 8, 22, 8, 0, 0, TimeSpan.Zero));
        var history = new RecentProcessIdentityHistory(TimeSpan.FromSeconds(30));
        history.Observe(new ProcessSnapshot(500, "Launcher", launcherPath, now.UtcNow.AddSeconds(-2)), now.UtcNow);
        history.Observe(new ProcessSnapshot(501, "RealGame", childPath, now.UtcNow.AddSeconds(-1), 500), now.UtcNow.AddSeconds(1));
        var parents = new FakeParentProvider();
        var collector = new WindowsProcessEvidenceCollector(new FakeGameConfigStore(), inspectLiveProcess: true, roleOverrides: new EmptyRoleOverrideStore(), parentProvider: parents, relationshipHistory: history, utcNow: now.GetUtcNow);
        now.Advance(TimeSpan.FromSeconds(2));
        var evidence = collector.Collect(new ProcessSnapshot(501, "RealGame", childPath, null));
        Assert.Contains(evidence.Evidence, item => item.Kind == GameDetectionEvidenceKind.ProcessRelationshipHistory && string.Equals(item.Detail, Path.GetFullPath(launcherPath), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReusedPidThatStartedAfterChildCannotBeRecoveredAsItsParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        var history = new RecentProcessIdentityHistory(TimeSpan.FromSeconds(30));
        var origin = new DateTimeOffset(2026, 8, 22, 8, 0, 0, TimeSpan.Zero);
        var childStarted = origin.AddSeconds(10);
        history.Observe(new ProcessSnapshot(600, "OldLauncher", Path.Combine(root, "OldLauncher.exe"), origin), origin);
        history.Observe(new ProcessSnapshot(600, "Unrelated", Path.Combine(root, "Unrelated.exe"), origin.AddSeconds(20)), origin.AddSeconds(20));
        Assert.Null(history.TryGetExecutablePath(600, origin.AddSeconds(21), childStarted));
    }

    private sealed class FakeGameConfigStore : IWindowsGameConfigStore
    {
        private readonly HashSet<string> _paths;
        public FakeGameConfigStore(params string[] paths) => _paths = paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        public bool ContainsExecutable(string executablePath) => _paths.Contains(Path.GetFullPath(executablePath));
    }

    private sealed class FakeParentProvider : IWindowsProcessParentProvider
    {
        public int? TryGetParentProcessId(int processId) => null;
        public string? TryGetExecutablePath(int processId) => null;
    }

    private sealed class EmptyRoleOverrideStore : IExecutableRoleOverrideStore
    {
        public bool TryGetRole(string executablePath, out ExecutableRole role) { role = ExecutableRole.Unknown; return false; }
        public void SetRole(string executablePath, ExecutableRole role) { }
        public void Remove(string executablePath) { }
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; private set; }
        public DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    }
}
