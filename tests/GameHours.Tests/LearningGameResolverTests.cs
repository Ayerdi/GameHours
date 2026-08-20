using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Tests;

public sealed class LearningGameResolverTests
{
    [Fact]
    public async Task FirstResolutionIsPersistedAndNextResolutionUsesExactPath()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Loose Game");
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "Loose", "game.exe");
        var inner = new CountingResolver(new GameResolution(game, 0.95, "loose_unreal_shipping"));
        var mappings = new FakeMappingRepository();
        var games = new FakeGameRepository();
        var resolver = new LearningGameResolver(inner, mappings, games);
        var process = new ProcessSnapshot(42, "game", path, null);

        var first = await resolver.ResolveAsync(process);
        var second = await resolver.ResolveAsync(process);

        Assert.Equal("loose_unreal_shipping", first.Method);
        Assert.Equal("learned_executable_path", second.Method);
        Assert.Equal(1.0, second.Confidence);
        Assert.Equal(game.Id, second.Game?.Id);
        Assert.Equal(1, inner.CallCount);
        Assert.NotNull(await mappings.FindByPathAsync(path));
    }

    [Fact]
    public async Task LowConfidenceResolutionIsNotLearned()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Weak Candidate");
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "Weak", "game.exe");
        var inner = new CountingResolver(new GameResolution(game, 0.50, "weak"));
        var mappings = new FakeMappingRepository();
        var games = new FakeGameRepository();
        var resolver = new LearningGameResolver(inner, mappings, games);
        var process = new ProcessSnapshot(43, "game", path, null);

        await resolver.ResolveAsync(process);
        await resolver.ResolveAsync(process);

        Assert.Equal(2, inner.CallCount);
        Assert.Null(await mappings.FindByPathAsync(path));
    }

    private sealed class CountingResolver : IGameResolver
    {
        private readonly GameResolution _resolution;

        public CountingResolver(GameResolution resolution) => _resolution = resolution;

        public int CallCount { get; private set; }

        public Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_resolution);
        }
    }

    private sealed class FakeMappingRepository : IExecutableMappingRepository
    {
        private readonly Dictionary<string, ExecutableMapping> _items =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ExecutableMapping?> FindByPathAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(Path.GetFullPath(executablePath), out var mapping);
            return Task.FromResult(mapping);
        }

        public Task UpsertAsync(ExecutableMapping mapping, CancellationToken cancellationToken = default)
        {
            _items[mapping.ExecutablePath] = mapping;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGameRepository : IGameRepository
    {
        private readonly Dictionary<Guid, TrackedGame> _games = new();

        public Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default)
        {
            _games[game.Id] = game;
            return Task.CompletedTask;
        }

        public Task<TrackedGame?> GetByIdAsync(Guid gameId, CancellationToken cancellationToken = default)
        {
            _games.TryGetValue(gameId, out var game);
            return Task.FromResult(game);
        }

        public Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackedGame>>(_games.Values.ToArray());
    }
}
