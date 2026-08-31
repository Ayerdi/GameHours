using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;

namespace GameHours.Tests;

public sealed class InstalledGameDiscoveryServiceTests
{
    [Fact]
    public async Task BrokenSourceDoesNotHideHealthySources()
    {
        var expected = new DiscoveredGame(
            Guid.NewGuid(),
            "Detected Game",
            GameDiscoverySource.Steam,
            "123",
            Path.GetTempPath(),
            null,
            1.0);
        var service = new InstalledGameDiscoveryService(
            new IInstalledGameSource[]
            {
                new ThrowingSource(),
                new StaticSource(expected)
            });

        var games = await service.DiscoverAsync();

        Assert.Equal(expected, Assert.Single(games));
    }

    [Fact]
    public async Task SourcesStartInParallel()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new InstalledGameDiscoveryService(
            new IInstalledGameSource[]
            {
                new CoordinatedSource("first", firstStarted, release.Task),
                new CoordinatedSource("second", secondStarted, release.Task)
            });

        var discovery = service.DiscoverAsync();

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        var games = await discovery;

        Assert.Empty(games);
    }

    private sealed class ThrowingSource : IInstalledGameSource
    {
        public string Name => "broken";
        public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class StaticSource : IInstalledGameSource
    {
        private readonly DiscoveredGame _game;
        public StaticSource(DiscoveredGame game) => _game = game;
        public string Name => "static";
        public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredGame>>(new[] { _game });
    }

    private sealed class CoordinatedSource : IInstalledGameSource
    {
        private readonly TaskCompletionSource _started;
        private readonly Task _release;

        public CoordinatedSource(string name, TaskCompletionSource started, Task release)
        {
            Name = name;
            _started = started;
            _release = release;
        }

        public string Name { get; }

        public async Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.WaitAsync(cancellationToken);
            return Array.Empty<DiscoveredGame>();
        }
    }
}
