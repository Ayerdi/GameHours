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
    public async Task LooseResolutionReusesExistingGameWithSameTitle()
    {
        var canonical = new TrackedGame(Guid.NewGuid(), "Gothic 1 Remake");
        var duplicate = new TrackedGame(Guid.NewGuid(), "Gothic 1 Remake");
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "Gothic", "G1R-Win64-Shipping.exe");
        var games = new FakeGameRepository();
        await games.UpsertAsync(canonical);
        var mappings = new FakeMappingRepository();
        var resolver = new LearningGameResolver(
            new CountingResolver(new GameResolution(duplicate, 0.95, "loose_unreal_shipping")),
            mappings,
            games);

        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(44, "g1r", path, null));
        var learned = await mappings.FindByPathAsync(path);

        Assert.Equal(canonical.Id, resolution.Game?.Id);
        Assert.Equal(canonical.Id, learned?.GameId);
    }

    [Fact]
    public async Task LearnedMappingToDuplicateGameSelfHealsToCanonicalTitle()
    {
        var canonical = new TrackedGame(Guid.NewGuid(), "Gothic 1 Remake");
        var duplicate = new TrackedGame(Guid.NewGuid(), "Gothic 1 Remake");
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "Gothic2", "G1R-Win64-Shipping.exe");
        var games = new FakeGameRepository();
        await games.UpsertAsync(canonical);
        await games.UpsertAsync(duplicate);
        var mappings = new FakeMappingRepository();
        await mappings.UpsertAsync(new ExecutableMapping(duplicate.Id, path, false));
        var inner = new CountingResolver(new GameResolution(duplicate, 0.95, "should_not_be_used"));
        var resolver = new LearningGameResolver(inner, mappings, games);

        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(45, "g1r", path, null));
        var healed = await mappings.FindByPathAsync(path);

        Assert.Equal("learned_executable_path", resolution.Method);
        Assert.Equal(canonical.Id, resolution.Game?.Id);
        Assert.Equal(canonical.Id, healed?.GameId);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task LearnedPrimaryMappingIsDiscardedWhenCurrentPolicyClassifiesHelper()
    {
        var staleGame = new TrackedGame(Guid.NewGuid(), "Platform Client");
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "Platform", "client.exe");
        var games = new FakeGameRepository();
        await games.UpsertAsync(staleGame);
        var mappings = new FakeMappingRepository();
        await mappings.UpsertAsync(new ExecutableMapping(staleGame.Id, path, false));
        var inner = new CountingResolver(
            new GameResolution(null, 0, "ignored_platform_launcher", true, ExecutableRole.Launcher),
            helperExecutable: true);
        var resolver = new LearningGameResolver(inner, mappings, games);

        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(46, "client", path, null));

        Assert.Null(resolution.Game);
        Assert.True(resolution.IsHelperProcess);
        Assert.Equal(ExecutableRole.Launcher, resolution.Role);
        Assert.Equal("ignored_platform_launcher", resolution.Method);
        Assert.Equal(1, inner.CallCount);
        Assert.Null(await mappings.FindByPathAsync(path));
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

    private sealed class CountingResolver : IGameResolver, IExecutableMappingValidationPolicy
    {
        private readonly GameResolution _resolution;
        private readonly bool _helperExecutable;

        public CountingResolver(GameResolution resolution, bool helperExecutable = false)
        {
            _resolution = resolution;
            _helperExecutable = helperExecutable;
        }

        public int CallCount { get; private set; }

        public bool IsHelperExecutable(string executablePath) => _helperExecutable;

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

        public Task DeleteByPathAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            _items.Remove(Path.GetFullPath(executablePath));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGameRepository : IGameRepository
    {
        private readonly Dictionary<Guid, TrackedGame> _games = new();
        private readonly List<Guid> _insertionOrder = new();

        public Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default)
        {
            if (!_games.ContainsKey(game.Id))
            {
                _insertionOrder.Add(game.Id);
            }

            _games[game.Id] = game;
            return Task.CompletedTask;
        }

        public Task<TrackedGame?> GetByIdAsync(Guid gameId, CancellationToken cancellationToken = default)
        {
            _games.TryGetValue(gameId, out var game);
            return Task.FromResult(game);
        }

        public Task<TrackedGame?> GetByTitleAsync(string title, CancellationToken cancellationToken = default)
        {
            var game = _insertionOrder
                .Select(id => _games[id])
                .FirstOrDefault(item => string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(game);
        }

        public Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackedGame>>(_insertionOrder.Select(id => _games[id]).ToArray());
    }
}
