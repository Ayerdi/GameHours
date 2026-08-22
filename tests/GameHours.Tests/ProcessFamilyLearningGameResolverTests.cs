using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Tests;

public sealed class ProcessFamilyLearningGameResolverTests
{
    [Fact]
    public async Task GraphicalChildOfLearnedHelperIsPromotedAndLearned()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Launcher Game");
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        var launcherPath = Path.Combine(root, "Launcher.exe");
        var childPath = Path.Combine(root, "bin", "RealGame.exe");
        var mappings = new FakeMappingRepository();
        await mappings.UpsertAsync(new ExecutableMapping(game.Id, launcherPath, true));
        var games = new FakeGameRepository();
        await games.UpsertAsync(game);

        var candidate = new TrackedGame(Guid.NewGuid(), "RealGame");
        var inner = new StaticResolver(new GameResolution(
            candidate,
            0.65,
            "heuristic_graphics_candidate",
            false,
            ExecutableRole.Unknown,
            new[]
            {
                new GameDetectionEvidence(
                    GameDetectionEvidenceKind.GraphicsRuntime,
                    0.15,
                    "graphics"),
                new GameDetectionEvidence(
                    GameDetectionEvidenceKind.VisibleWindow,
                    0.10,
                    "window"),
                new GameDetectionEvidence(
                    GameDetectionEvidenceKind.ProcessRelationship,
                    0.0,
                    launcherPath)
            }));
        var resolver = new LearningGameResolver(inner, mappings, games);

        var resolution = await resolver.ResolveAsync(
            new ProcessSnapshot(700, "RealGame", childPath, null));

        Assert.Equal(game.Id, resolution.Game?.Id);
        Assert.Equal(0.90, resolution.Confidence);
        Assert.Equal("learned_parent_process_family", resolution.Method);
        Assert.Equal(ExecutableRole.PrimaryGame, resolution.Role);
        var learnedChild = await mappings.FindByPathAsync(childPath);
        Assert.Equal(game.Id, learnedChild?.GameId);
        Assert.False(learnedChild?.IsHelper);
    }

    [Fact]
    public async Task GraphicalChildCanUseRecentlyObservedLearnedHelperAfterParentExit()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Grace Launcher Game");
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        var launcherPath = Path.Combine(root, "Launcher.exe");
        var childPath = Path.Combine(root, "RealGame.exe");
        var mappings = new FakeMappingRepository();
        await mappings.UpsertAsync(new ExecutableMapping(game.Id, launcherPath, true));
        var games = new FakeGameRepository();
        await games.UpsertAsync(game);

        var resolver = new LearningGameResolver(
            new StaticResolver(new GameResolution(
                new TrackedGame(Guid.NewGuid(), "RealGame"),
                0.65,
                "heuristic_graphics_candidate",
                false,
                ExecutableRole.Unknown,
                new[]
                {
                    new GameDetectionEvidence(
                        GameDetectionEvidenceKind.GraphicsRuntime,
                        0.15,
                        "graphics"),
                    new GameDetectionEvidence(
                        GameDetectionEvidenceKind.VisibleWindow,
                        0.10,
                        "window"),
                    new GameDetectionEvidence(
                        GameDetectionEvidenceKind.ProcessRelationshipHistory,
                        0.0,
                        launcherPath)
                })),
            mappings,
            games);

        var resolution = await resolver.ResolveAsync(
            new ProcessSnapshot(702, "RealGame", childPath, null));

        Assert.Equal(game.Id, resolution.Game?.Id);
        Assert.Equal(0.88, resolution.Confidence);
        Assert.Equal("learned_recent_parent_process_family", resolution.Method);
        Assert.Equal(ExecutableRole.PrimaryGame, resolution.Role);
        var learnedChild = await mappings.FindByPathAsync(childPath);
        Assert.Equal(game.Id, learnedChild?.GameId);
        Assert.False(learnedChild?.IsHelper);
    }

    [Fact]
    public async Task ParentRelationshipDoesNotPromoteWithoutLearnedHelper()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        var parentPath = Path.Combine(root, "UnknownParent.exe");
        var childPath = Path.Combine(root, "RealGame.exe");
        var candidate = new TrackedGame(Guid.NewGuid(), "RealGame");
        var mappings = new FakeMappingRepository();
        var games = new FakeGameRepository();
        var resolver = new LearningGameResolver(
            new StaticResolver(new GameResolution(
                candidate,
                0.65,
                "heuristic_graphics_candidate",
                false,
                ExecutableRole.Unknown,
                new[]
                {
                    new GameDetectionEvidence(
                        GameDetectionEvidenceKind.ProcessRelationship,
                        0.0,
                        parentPath)
                })),
            mappings,
            games);

        var resolution = await resolver.ResolveAsync(
            new ProcessSnapshot(701, "RealGame", childPath, null));

        Assert.Equal(0.65, resolution.Confidence);
        Assert.Equal("heuristic_graphics_candidate", resolution.Method);
        Assert.Null(await mappings.FindByPathAsync(childPath));
    }

    private sealed class StaticResolver : IGameResolver
    {
        private readonly GameResolution _resolution;

        public StaticResolver(GameResolution resolution) => _resolution = resolution;

        public Task<GameResolution> ResolveAsync(
            ProcessSnapshot process,
            CancellationToken cancellationToken = default) => Task.FromResult(_resolution);
    }

    private sealed class FakeMappingRepository : IExecutableMappingRepository
    {
        private readonly Dictionary<string, ExecutableMapping> _items =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ExecutableMapping?> FindByPathAsync(
            string executablePath,
            CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(Path.GetFullPath(executablePath), out var value);
            return Task.FromResult(value);
        }

        public Task UpsertAsync(
            ExecutableMapping mapping,
            CancellationToken cancellationToken = default)
        {
            _items[mapping.ExecutablePath] = mapping;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGameRepository : IGameRepository
    {
        private readonly Dictionary<Guid, TrackedGame> _items = new();

        public Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default)
        {
            _items[game.Id] = game;
            return Task.CompletedTask;
        }

        public Task<TrackedGame?> GetByIdAsync(Guid gameId, CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(gameId, out var game);
            return Task.FromResult(game);
        }

        public Task<TrackedGame?> GetByTitleAsync(string title, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Values.FirstOrDefault(
                game => string.Equals(game.Title, title, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackedGame>>(_items.Values.ToArray());
    }
}
