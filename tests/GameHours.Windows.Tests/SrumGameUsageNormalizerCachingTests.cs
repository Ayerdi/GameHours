using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;
using GameHours.Windows.Srum;

namespace GameHours.Windows.Tests;

public sealed class SrumGameUsageNormalizerCachingTests
{
    [Fact]
    public async Task NormalizeAsync_ClassifiesRepeatedExecutablePathOnlyOnce()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Repeated SRUM Game");
        var executablePath = Path.Combine(Path.GetTempPath(), "RepeatedSrumGame.exe");
        var mappings = new CountingMappingRepository(
            new ExecutableMapping(game.Id, executablePath, false));
        var games = new CountingGameRepository(game);
        var normalizer = new SrumGameUsageNormalizer(
            mappings,
            games,
            new UnexpectedResolver());
        var firstTimestamp = DateTimeOffset.Parse("2026-08-27T10:00:00Z");
        var rows = Enumerable.Range(0, 100)
            .Select(index => new SrumApplicationUsage(
                1,
                executablePath,
                null,
                firstTimestamp.AddHours(index),
                TimeSpan.FromMinutes(1)))
            .ToArray();

        var result = await normalizer.NormalizeAsync(rows);

        Assert.Equal(1, mappings.FindCalls);
        Assert.Equal(1, games.GetByIdCalls);
        Assert.Equal(100, result.Decisions.Count);
        Assert.All(result.Decisions, decision =>
            Assert.Equal("accepted_exact_mapping", decision.Decision));
        var usage = Assert.Single(result.Games);
        Assert.Equal(100, usage.SourceRows);
        Assert.Equal(100, usage.SelectedRows);
    }

    private sealed class CountingMappingRepository : IExecutableMappingRepository
    {
        private readonly ExecutableMapping _mapping;

        public CountingMappingRepository(ExecutableMapping mapping)
        {
            _mapping = mapping;
        }

        public int FindCalls { get; private set; }

        public Task<ExecutableMapping?> FindByPathAsync(
            string executablePath,
            CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return Task.FromResult<ExecutableMapping?>(
                string.Equals(
                    executablePath,
                    _mapping.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                    ? _mapping
                    : null);
        }

        public Task UpsertAsync(
            ExecutableMapping mapping,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingGameRepository : IGameRepository
    {
        private readonly TrackedGame _game;

        public CountingGameRepository(TrackedGame game)
        {
            _game = game;
        }

        public int GetByIdCalls { get; private set; }

        public Task UpsertAsync(
            TrackedGame game,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrackedGame?> GetByIdAsync(
            Guid gameId,
            CancellationToken cancellationToken = default)
        {
            GetByIdCalls++;
            return Task.FromResult<TrackedGame?>(gameId == _game.Id ? _game : null);
        }

        public Task<TrackedGame?> GetByTitleAsync(
            string title,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TrackedGame?>(null);

        public Task<IReadOnlyList<TrackedGame>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackedGame>>(new[] { _game });
    }

    private sealed class UnexpectedResolver : IGameResolver
    {
        public Task<GameResolution> ResolveAsync(
            ProcessSnapshot process,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Exact mapping should avoid fallback game resolution.");
    }
}
