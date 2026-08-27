using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
using GameHours.Windows.Discovery;

namespace GameHours.Windows.Tests;

public sealed class GooglePlayGamesClassificationTests
{
    [Fact]
    public async Task GooglePlayGamesClientIsAlwaysTreatedAsPlatformLauncher()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "GameHours.Windows.Tests",
            Guid.NewGuid().ToString("N"),
            "GooglePlayGames.exe");
        var resolver = new WindowsGameResolver(
            Array.Empty<DiscoveredGame>(),
            new WindowsProcessEvidenceCollector(
                new FakeGameConfigStore(path),
                inspectLiveProcess: false));

        var resolution = await resolver.ResolveAsync(
            new ProcessSnapshot(100, "GooglePlayGames", path, null));

        Assert.True(WindowsGameResolver.IsHelperExecutable(path));
        Assert.Null(resolution.Game);
        Assert.True(resolution.IsHelperProcess);
        Assert.Equal(ExecutableRole.Launcher, resolution.Role);
        Assert.Equal("ignored_platform_launcher", resolution.Method);
    }

    private sealed class FakeGameConfigStore : IWindowsGameConfigStore
    {
        private readonly string _path;

        public FakeGameConfigStore(string path)
        {
            _path = Path.GetFullPath(path);
        }

        public bool ContainsExecutable(string executablePath) =>
            string.Equals(
                Path.GetFullPath(executablePath),
                _path,
                StringComparison.OrdinalIgnoreCase);
    }
}
