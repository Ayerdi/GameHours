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
}
