using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;
using GameHours.Core.Tracking;

namespace GameHours.Tests;

public sealed class GameSessionEngineTests
{
    [Fact]
    public async Task MultiplePrimaryProcessesProduceOneGameSession()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Test Game");
        var start = new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);
        var monitor = new FakeMonitor(
            new ProcessObservation(10, "game", @"C:\Games\Test\game.exe", start, ProcessObservationType.ReconciledStart),
            new ProcessObservation(11, "game-child", @"C:\Games\Test\child.exe", start.AddSeconds(2), ProcessObservationType.ReconciledStart),
            new ProcessObservation(10, "game", @"C:\Games\Test\game.exe", start.AddSeconds(5), ProcessObservationType.ReconciledStop),
            new ProcessObservation(11, "game-child", @"C:\Games\Test\child.exe", start.AddSeconds(9), ProcessObservationType.ReconciledStop));
        var sessions = new FakeSessionRepository();
        var engine = new GameSessionEngine(
            monitor,
            new FakeResolver(game),
            new FakeGameRepository(),
            sessions,
            new FakeTrackingStateRepository(start.AddSeconds(-1)),
            timeProvider: new FixedTimeProvider(start.AddSeconds(-1)));

        await engine.RunAsync();

        var session = Assert.Single(sessions.Items);
        Assert.Equal(start, session.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(9), session.Duration);
        Assert.Equal(Confidence.High, session.Confidence);
        Assert.Equal(CaptureMethod.Reconciliation, session.CaptureMethod);
    }

    [Fact]
    public async Task InitialSnapshotStartsAtCutoverNotProcessLifetime()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Already Running");
        var cutover = new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);
        var monitor = new FakeMonitor(
            new ProcessObservation(20, "game", @"C:\Games\Running\game.exe", cutover, ProcessObservationType.InitialSnapshot),
            new ProcessObservation(20, "game", @"C:\Games\Running\game.exe", cutover.AddSeconds(12), ProcessObservationType.ReconciledStop));
        var sessions = new FakeSessionRepository();
        var engine = new GameSessionEngine(
            monitor,
            new FakeResolver(game),
            new FakeGameRepository(),
            sessions,
            new FakeTrackingStateRepository(cutover),
            timeProvider: new FixedTimeProvider(cutover));

        await engine.RunAsync();

        var session = Assert.Single(sessions.Items);
        Assert.Equal(cutover, session.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(12), session.Duration);
        Assert.Equal(CaptureMethod.InitialSnapshot, session.CaptureMethod);
    }

    [Fact]
    public async Task InitialSnapshotAfterRestartStartsAtCurrentRunNotOriginalCutover()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Restarted Tracker");
        var cutover = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var runStartedAt = cutover.AddHours(6);
        var monitor = new FakeMonitor(
            new ProcessObservation(30, "game", @"C:\Games\Restart\game.exe", runStartedAt, ProcessObservationType.InitialSnapshot),
            new ProcessObservation(30, "game", @"C:\Games\Restart\game.exe", runStartedAt.AddSeconds(10), ProcessObservationType.ReconciledStop));
        var sessions = new FakeSessionRepository();
        var engine = new GameSessionEngine(
            monitor,
            new FakeResolver(game),
            new FakeGameRepository(),
            sessions,
            new FakeTrackingStateRepository(cutover),
            timeProvider: new FixedTimeProvider(runStartedAt));

        await engine.RunAsync();

        var session = Assert.Single(sessions.Items);
        Assert.Equal(runStartedAt, session.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(10), session.Duration);
    }

    private sealed class FakeMonitor : IProcessMonitor
    {
        private readonly ProcessObservation[] _observations;

        public FakeMonitor(params ProcessObservation[] observations) => _observations = observations;

        public async IAsyncEnumerable<ProcessObservation> ObserveAsync(CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var observation in _observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return observation;
            }
        }
    }

    private sealed class FakeResolver : IGameResolver
    {
        private readonly TrackedGame _game;
        public FakeResolver(TrackedGame game) => _game = game;

        public Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameResolution(_game, 1.0, "test"));
    }

    private sealed class FakeGameRepository : IGameRepository
    {
        public Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackedGame>>(Array.Empty<TrackedGame>());
    }

    private sealed class FakeSessionRepository : ISessionRepository
    {
        public List<PlaySession> Items { get; } = new();

        public Task<bool> AddAsync(PlaySession session, CancellationToken cancellationToken = default)
        {
            Items.Add(session);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<PlaySession>> GetForGameAsync(Guid gameId, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaySession>>(Items.Where(item => item.GameId == gameId).ToArray());

        public Task<bool> HasOverlapAsync(Guid gameId, DateTimeOffset periodStartUtc, DateTimeOffset periodEndUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FakeTrackingStateRepository : ITrackingStateRepository
    {
        private readonly DateTimeOffset _cutover;
        public FakeTrackingStateRepository(DateTimeOffset cutover) => _cutover = cutover;

        public Task<DateTimeOffset?> GetTrackingStartedAtAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(_cutover);

        public Task<DateTimeOffset> GetOrSetTrackingStartedAtAsync(DateTimeOffset proposedUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(_cutover);
    }
}
