using System.Runtime.CompilerServices;
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
            new FakeOpenSessionRepository(),
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
    public async Task DifferentGamesCanRunConcurrentlyWithoutMergingSessions()
    {
        var firstGame = new TrackedGame(Guid.NewGuid(), "First Game");
        var secondGame = new TrackedGame(Guid.NewGuid(), "Second Game");
        var start = new DateTimeOffset(2026, 8, 24, 17, 0, 0, TimeSpan.Zero);
        var monitor = new FakeMonitor(
            new ProcessObservation(20, "first", @"C:\Games\First\first.exe", start, ProcessObservationType.ReconciledStart),
            new ProcessObservation(30, "second", @"C:\Games\Second\second.exe", start.AddSeconds(2), ProcessObservationType.ReconciledStart),
            new ProcessObservation(20, "first", @"C:\Games\First\first.exe", start.AddSeconds(8), ProcessObservationType.ReconciledStop),
            new ProcessObservation(30, "second", @"C:\Games\Second\second.exe", start.AddSeconds(10), ProcessObservationType.ReconciledStop));
        var sessions = new FakeSessionRepository();
        var resolver = new FakeResolverByProcess(new Dictionary<string, TrackedGame>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = firstGame,
            ["second"] = secondGame
        });
        var engine = new GameSessionEngine(
            monitor,
            resolver,
            new FakeGameRepository(),
            sessions,
            new FakeOpenSessionRepository(),
            new FakeTrackingStateRepository(start.AddSeconds(-1)),
            timeProvider: new FixedTimeProvider(start.AddSeconds(-1)));

        await engine.RunAsync();

        Assert.Equal(2, sessions.Items.Count);
        var firstSession = Assert.Single(sessions.Items, item => item.GameId == firstGame.Id);
        var secondSession = Assert.Single(sessions.Items, item => item.GameId == secondGame.Id);
        Assert.Equal(start, firstSession.StartedAtUtc);
        Assert.Equal(start.AddSeconds(8), firstSession.EndedAtUtc);
        Assert.Equal(start.AddSeconds(2), secondSession.StartedAtUtc);
        Assert.Equal(start.AddSeconds(10), secondSession.EndedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(8), firstSession.Duration);
        Assert.Equal(TimeSpan.FromSeconds(8), secondSession.Duration);
    }

    [Fact]
    public async Task SleepSegmentationDoesNotCountSuspendedWallClockTime()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Sleeping Game");
        var startedAt = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.Zero);
        var suspendedAt = startedAt.AddMinutes(5);
        var resumedAt = suspendedAt.AddMinutes(20);
        var endedAt = resumedAt.AddMinutes(3);
        var monitor = new FakeMonitor(
            new ProcessObservation(40, "game", @"C:\Games\Sleep\game.exe", startedAt, ProcessObservationType.ReconciledStart),
            new ProcessObservation(40, "game", @"C:\Games\Sleep\game.exe", suspendedAt, ProcessObservationType.ReconciledStop),
            new ProcessObservation(40, "game", @"C:\Games\Sleep\game.exe", resumedAt, ProcessObservationType.ReconciledStart),
            new ProcessObservation(40, "game", @"C:\Games\Sleep\game.exe", endedAt, ProcessObservationType.ReconciledStop));
        var sessions = new FakeSessionRepository();
        var engine = new GameSessionEngine(
            monitor,
            new FakeResolver(game),
            new FakeGameRepository(),
            sessions,
            new FakeOpenSessionRepository(),
            new FakeTrackingStateRepository(startedAt.AddSeconds(-1)),
            timeProvider: new FixedTimeProvider(startedAt.AddSeconds(-1)));

        await engine.RunAsync();

        Assert.Equal(2, sessions.Items.Count);
        var ordered = sessions.Items.OrderBy(session => session.StartedAtUtc).ToArray();
        Assert.Equal(startedAt, ordered[0].StartedAtUtc);
        Assert.Equal(suspendedAt, ordered[0].EndedAtUtc);
        Assert.Equal(resumedAt, ordered[1].StartedAtUtc);
        Assert.Equal(endedAt, ordered[1].EndedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(8), ordered.Sum(session => session.Duration));
        Assert.DoesNotContain(ordered, session =>
            session.StartedAtUtc < resumedAt && session.EndedAtUtc > suspendedAt);
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
            new FakeOpenSessionRepository(),
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
            new FakeOpenSessionRepository(),
            new FakeTrackingStateRepository(cutover),
            timeProvider: new FixedTimeProvider(runStartedAt));

        await engine.RunAsync();

        var session = Assert.Single(sessions.Items);
        Assert.Equal(runStartedAt, session.StartedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(10), session.Duration);
    }

    [Fact]
    public async Task InterruptedSessionIsRecoveredOnlyThroughLastCheckpoint()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Interrupted Game");
        var startedAt = new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero);
        var checkpointAt = startedAt.AddSeconds(20);
        var sessionId = Guid.NewGuid();
        var games = new FakeGameRepository();
        await games.UpsertAsync(game);
        var openSessions = new FakeOpenSessionRepository();
        await openSessions.UpsertAsync(new OpenSessionCheckpoint(
            sessionId,
            game.Id,
            startedAt,
            checkpointAt,
            CaptureMethod.Reconciliation));
        var sessions = new FakeSessionRepository();
        var notices = new List<TrackingNotice>();
        var engine = new GameSessionEngine(
            new FakeMonitor(),
            new FakeResolver(game),
            games,
            sessions,
            openSessions,
            new FakeTrackingStateRepository(startedAt.AddSeconds(-1)),
            timeProvider: new FixedTimeProvider(checkpointAt.AddMinutes(5)));
        engine.Notice += notices.Add;

        await engine.RunAsync();

        var recovered = Assert.Single(sessions.Items);
        Assert.Equal(sessionId, recovered.Id);
        Assert.Equal(startedAt, recovered.StartedAtUtc);
        Assert.Equal(checkpointAt, recovered.EndedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(20), recovered.Duration);
        Assert.Equal("RecoveredFromCheckpoint", recovered.EndReason);
        Assert.Empty(openSessions.Items);
        Assert.Contains(notices, notice => notice.Type == TrackingNoticeType.SessionRecovered);
    }

    private sealed class FakeMonitor : IProcessMonitor
    {
        private readonly ProcessObservation[] _observations;

        public FakeMonitor(params ProcessObservation[] observations) => _observations = observations;

        public async IAsyncEnumerable<ProcessObservation> ObserveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    private sealed class FakeResolverByProcess : IGameResolver
    {
        private readonly IReadOnlyDictionary<string, TrackedGame> _gamesByProcess;

        public FakeResolverByProcess(IReadOnlyDictionary<string, TrackedGame> gamesByProcess) =>
            _gamesByProcess = gamesByProcess;

        public Task<GameResolution> ResolveAsync(
            ProcessSnapshot process,
            CancellationToken cancellationToken = default)
        {
            var game = _gamesByProcess[process.ProcessName];
            return Task.FromResult(new GameResolution(game, 1.0, "test"));
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

        public Task<TrackedGame?> GetByTitleAsync(string title, CancellationToken cancellationToken = default) =>
            Task.FromResult(_games.Values.FirstOrDefault(game =>
                string.Equals(game.Title, title, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackedGame>>(_games.Values.ToArray());
    }

    private sealed class FakeSessionRepository : ISessionRepository
    {
        public List<PlaySession> Items { get; } = new();

        public Task<bool> AddAsync(PlaySession session, CancellationToken cancellationToken = default)
        {
            if (Items.Any(item => item.Id == session.Id))
            {
                return Task.FromResult(false);
            }

            Items.Add(session);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<PlaySession>> GetForGameAsync(Guid gameId, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaySession>>(Items.Where(item => item.GameId == gameId).ToArray());

        public Task<bool> HasOverlapAsync(Guid gameId, DateTimeOffset periodStartUtc, DateTimeOffset periodEndUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeOpenSessionRepository : IOpenSessionRepository
    {
        public List<OpenSessionCheckpoint> Items { get; } = new();

        public Task UpsertAsync(OpenSessionCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            var index = Items.FindIndex(item => item.SessionId == checkpoint.SessionId);
            if (index >= 0)
            {
                Items[index] = checkpoint;
            }
            else
            {
                Items.Add(checkpoint);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OpenSessionCheckpoint>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpenSessionCheckpoint>>(Items.ToArray());

        public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Items.RemoveAll(item => item.SessionId == sessionId);
            return Task.CompletedTask;
        }
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
