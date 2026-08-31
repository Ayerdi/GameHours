using System.Runtime.CompilerServices;
using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;
using GameHours.Core.Tracking;

namespace GameHours.Tests;

public sealed class GameSessionEngineLifecycleTests
{
    [Fact]
    public async Task GracefulCancellationFinalizesActiveSessionAndDeletesCheckpoint()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Graceful Game");
        var startedAt = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
        var shutdownAt = startedAt.AddSeconds(12.345);
        var monitor = new BlockingMonitor(new ProcessObservation(
            101,
            "game",
            @"C:\Games\Graceful\game.exe",
            startedAt,
            ProcessObservationType.ReconciledStart));
        var sessions = new FakeSessionRepository();
        var openSessions = new FakeOpenSessionRepository();
        var time = new MutableTimeProvider(startedAt);
        using var cancellation = new CancellationTokenSource();
        var notices = new List<TrackingNotice>();
        var engine = new GameSessionEngine(
            monitor,
            new FakeResolver(game),
            new FakeGameRepository(),
            sessions,
            openSessions,
            new FakeTrackingStateRepository(startedAt.AddSeconds(-1)),
            timeProvider: time,
            checkpointInterval: TimeSpan.FromMinutes(1));
        engine.Notice += notices.Add;

        var run = engine.RunAsync(cancellation.Token);
        await monitor.WaitingForCancellation;

        time.UtcNow = shutdownAt;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var session = Assert.Single(sessions.Items);
        Assert.Equal(startedAt, session.StartedAtUtc);
        Assert.Equal(shutdownAt, session.EndedAtUtc);
        Assert.Equal(shutdownAt - startedAt, session.Duration);
        Assert.Equal("GracefulShutdown", session.EndReason);
        Assert.Empty(openSessions.Items);
        Assert.Contains(notices, notice =>
            notice.Type == TrackingNoticeType.SessionCompleted &&
            notice.AtUtc == shutdownAt &&
            notice.Detail == "GracefulShutdown");
    }

    [Fact]
    public async Task UnexpectedMonitorFailureLeavesCheckpointForRecovery()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Failed Monitor Game");
        var startedAt = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
        var failureAt = startedAt.AddSeconds(7);
        var sessions = new FakeSessionRepository();
        var openSessions = new FakeOpenSessionRepository();
        var engine = new GameSessionEngine(
            new FailingMonitor(new ProcessObservation(
                202,
                "game",
                @"C:\Games\Failure\game.exe",
                startedAt,
                ProcessObservationType.ReconciledStart)),
            new FakeResolver(game),
            new FakeGameRepository(),
            sessions,
            openSessions,
            new FakeTrackingStateRepository(startedAt.AddSeconds(-1)),
            timeProvider: new MutableTimeProvider(failureAt),
            checkpointInterval: TimeSpan.FromMinutes(1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync());

        Assert.Equal("synthetic monitor failure", exception.Message);
        Assert.Empty(sessions.Items);
        var checkpoint = Assert.Single(openSessions.Items);
        Assert.Equal(startedAt, checkpoint.StartedAtUtc);
        Assert.Equal(failureAt, checkpoint.LastCheckpointAtUtc);
    }

    private sealed class BlockingMonitor : IProcessMonitor
    {
        private readonly ProcessObservation _start;
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingMonitor(ProcessObservation start) => _start = start;

        public Task WaitingForCancellation => _waiting.Task;

        public async IAsyncEnumerable<ProcessObservation> ObserveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return _start;
            _waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingMonitor : IProcessMonitor
    {
        private readonly ProcessObservation _start;
        public FailingMonitor(ProcessObservation start) => _start = start;

        public async IAsyncEnumerable<ProcessObservation> ObserveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return _start;
            throw new InvalidOperationException("synthetic monitor failure");
        }
    }

    private sealed class FakeResolver : IGameResolver
    {
        private readonly TrackedGame _game;
        public FakeResolver(TrackedGame game) => _game = game;

        public Task<GameResolution> ResolveAsync(
            ProcessSnapshot process,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameResolution(_game, 1.0, "test"));
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

        public Task<TrackedGame?> GetByTitleAsync(
            string title,
            CancellationToken cancellationToken = default) =>
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

        public Task<IReadOnlyList<PlaySession>> GetForGameAsync(
            Guid gameId,
            DateTimeOffset? fromUtc = null,
            DateTimeOffset? toUtc = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlaySession>>(Items.Where(item => item.GameId == gameId).ToArray());

        public Task<bool> HasOverlapAsync(
            Guid gameId,
            DateTimeOffset periodStartUtc,
            DateTimeOffset periodEndUtc,
            CancellationToken cancellationToken = default) =>
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

    private sealed class FakeTrackingStateRepository : ITrackingStateRepository
    {
        private readonly DateTimeOffset _cutover;
        public FakeTrackingStateRepository(DateTimeOffset cutover) => _cutover = cutover;

        public Task<DateTimeOffset?> GetTrackingStartedAtAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(_cutover);

        public Task<DateTimeOffset> GetOrSetTrackingStartedAtAsync(
            DateTimeOffset proposedUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_cutover);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public MutableTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
