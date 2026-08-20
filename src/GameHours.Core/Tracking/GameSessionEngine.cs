using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Core.Tracking;

public enum TrackingNoticeType
{
    SessionStarted = 1,
    SessionCompleted = 2,
    SessionRecovered = 3
}

public sealed record TrackingNotice(
    TrackingNoticeType Type,
    TrackedGame Game,
    DateTimeOffset AtUtc,
    TimeSpan? Duration = null,
    string? Detail = null);

public sealed class GameSessionEngine
{
    private readonly IProcessMonitor _monitor;
    private readonly IGameResolver _resolver;
    private readonly IGameRepository _games;
    private readonly ISessionRepository _sessions;
    private readonly IOpenSessionRepository _openSessions;
    private readonly ITrackingStateRepository _trackingState;
    private readonly double _minimumResolutionConfidence;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _checkpointInterval;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private readonly Dictionary<int, Guid> _processToGame = new();
    private readonly Dictionary<Guid, ActiveGame> _activeGames = new();

    public event Action<TrackingNotice>? Notice;

    public GameSessionEngine(
        IProcessMonitor monitor,
        IGameResolver resolver,
        IGameRepository games,
        ISessionRepository sessions,
        IOpenSessionRepository openSessions,
        ITrackingStateRepository trackingState,
        double minimumResolutionConfidence = 0.80,
        TimeProvider? timeProvider = null,
        TimeSpan? checkpointInterval = null)
    {
        if (minimumResolutionConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumResolutionConfidence));
        }

        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _openSessions = openSessions ?? throw new ArgumentNullException(nameof(openSessions));
        _trackingState = trackingState ?? throw new ArgumentNullException(nameof(trackingState));
        _minimumResolutionConfidence = minimumResolutionConfidence;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkpointInterval = checkpointInterval ?? TimeSpan.FromSeconds(5);
        if (_checkpointInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointInterval));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var runStartedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        await RecoverInterruptedSessionsAsync(cancellationToken);

        var cutover = await _trackingState.GetOrSetTrackingStartedAtAsync(
            runStartedAt,
            cancellationToken);

        using var checkpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var checkpointTask = CheckpointLoopAsync(checkpointCancellation.Token);

        try
        {
            await foreach (var observation in _monitor.ObserveAsync(cancellationToken))
            {
                if (IsStart(observation.Type))
                {
                    await HandleStartAsync(observation, cutover, runStartedAt, cancellationToken);
                }
                else
                {
                    await HandleStopAsync(observation, cancellationToken);
                }
            }
        }
        finally
        {
            checkpointCancellation.Cancel();
            try
            {
                await checkpointTask;
            }
            catch (OperationCanceledException) when (checkpointCancellation.IsCancellationRequested)
            {
            }

            var stoppedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            if (cancellationToken.IsCancellationRequested)
            {
                // Cancellation is the intentional shutdown contract used by Ctrl+C today and
                // by the future tray/UI/update coordinator. It is not a crash: close every
                // observed segment at the actual shutdown boundary and remove its checkpoint.
                await FinalizeActiveSessionsAsync(
                    stoppedAt,
                    "GracefulShutdown",
                    CancellationToken.None);
            }
            else
            {
                // Natural/exceptional monitor termination is not known to be intentional.
                // Preserve conservative crash recovery semantics instead of inventing an exact
                // end boundary that the caller did not request.
                await PersistActiveCheckpointsAsync(stoppedAt, CancellationToken.None);
            }
        }
    }

    private async Task RecoverInterruptedSessionsAsync(CancellationToken cancellationToken)
    {
        var interrupted = await _openSessions.GetAllAsync(cancellationToken);
        foreach (var checkpoint in interrupted)
        {
            var inserted = false;
            if (checkpoint.LastCheckpointAtUtc > checkpoint.StartedAtUtc)
            {
                var session = new PlaySession(
                    checkpoint.SessionId,
                    checkpoint.GameId,
                    checkpoint.StartedAtUtc,
                    checkpoint.LastCheckpointAtUtc,
                    LessPrecise(checkpoint.CaptureMethod, CaptureMethod.Reconciliation),
                    Confidence.High,
                    "RecoveredFromCheckpoint");

                inserted = await _sessions.AddAsync(session, cancellationToken);
            }

            await _openSessions.DeleteAsync(checkpoint.SessionId, cancellationToken);

            if (!inserted)
            {
                continue;
            }

            var game = await _games.GetByIdAsync(checkpoint.GameId, cancellationToken);
            if (game is not null)
            {
                Notice?.Invoke(new TrackingNotice(
                    TrackingNoticeType.SessionRecovered,
                    game,
                    checkpoint.LastCheckpointAtUtc,
                    checkpoint.LastCheckpointAtUtc - checkpoint.StartedAtUtc,
                    "RecoveredFromCheckpoint"));
            }
        }
    }

    private async Task HandleStartAsync(
        ProcessObservation observation,
        DateTimeOffset cutover,
        DateTimeOffset runStartedAt,
        CancellationToken cancellationToken)
    {
        if (_processToGame.ContainsKey(observation.ProcessId))
        {
            return;
        }

        var resolution = await _resolver.ResolveAsync(
            new ProcessSnapshot(
                observation.ProcessId,
                observation.ProcessName,
                observation.ExecutablePath,
                null),
            cancellationToken);

        if (resolution.Game is null || resolution.IsHelper || resolution.Confidence < _minimumResolutionConfidence)
        {
            return;
        }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (_processToGame.ContainsKey(observation.ProcessId))
            {
                return;
            }

            var game = resolution.Game;
            _processToGame[observation.ProcessId] = game.Id;

            if (!_activeGames.TryGetValue(game.Id, out var active))
            {
                var startedAt = observation.Type is ProcessObservationType.InitialSnapshot
                    ? Max(runStartedAt, cutover)
                    : Max(observation.OccurredAtUtc, cutover);

                active = new ActiveGame(
                    Guid.NewGuid(),
                    game,
                    startedAt,
                    CaptureMethodFor(observation.Type));
                _activeGames.Add(game.Id, active);
                await _games.UpsertAsync(game, cancellationToken);

                Notice?.Invoke(new TrackingNotice(
                    TrackingNoticeType.SessionStarted,
                    game,
                    startedAt,
                    Detail: resolution.Method));
            }
            else
            {
                active.CaptureMethod = LessPrecise(active.CaptureMethod, CaptureMethodFor(observation.Type));
            }

            active.ProcessIds.Add(observation.ProcessId);
            await _openSessions.UpsertAsync(
                CheckpointFor(active, Max(observation.OccurredAtUtc, active.StartedAtUtc)),
                cancellationToken);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task HandleStopAsync(
        ProcessObservation observation,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!_processToGame.Remove(observation.ProcessId, out var gameId) ||
                !_activeGames.TryGetValue(gameId, out var active))
            {
                return;
            }

            active.ProcessIds.Remove(observation.ProcessId);
            active.CaptureMethod = LessPrecise(active.CaptureMethod, CaptureMethodFor(observation.Type));

            if (active.ProcessIds.Count > 0)
            {
                await _openSessions.UpsertAsync(
                    CheckpointFor(active, Max(observation.OccurredAtUtc, active.StartedAtUtc)),
                    cancellationToken);
                return;
            }

            _activeGames.Remove(gameId);
            var endedAt = observation.OccurredAtUtc.ToUniversalTime();
            if (endedAt <= active.StartedAtUtc)
            {
                await _openSessions.DeleteAsync(active.SessionId, cancellationToken);
                return;
            }

            var session = new PlaySession(
                active.SessionId,
                gameId,
                active.StartedAtUtc,
                endedAt,
                active.CaptureMethod,
                Confidence.High,
                observation.Type.ToString());

            var inserted = await _sessions.AddAsync(session, cancellationToken);
            await _openSessions.DeleteAsync(active.SessionId, cancellationToken);

            if (inserted)
            {
                Notice?.Invoke(new TrackingNotice(
                    TrackingNoticeType.SessionCompleted,
                    active.Game,
                    endedAt,
                    session.Duration,
                    observation.Type.ToString()));
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task CheckpointLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_checkpointInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await PersistActiveCheckpointsAsync(
                _timeProvider.GetUtcNow().ToUniversalTime(),
                cancellationToken);
        }
    }

    private async Task FinalizeActiveSessionsAsync(
        DateTimeOffset endedAtUtc,
        string endReason,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var active in _activeGames.Values.ToArray())
            {
                if (endedAtUtc > active.StartedAtUtc)
                {
                    var session = new PlaySession(
                        active.SessionId,
                        active.Game.Id,
                        active.StartedAtUtc,
                        endedAtUtc,
                        active.CaptureMethod,
                        Confidence.High,
                        endReason);

                    var inserted = await _sessions.AddAsync(session, cancellationToken);
                    if (inserted)
                    {
                        Notice?.Invoke(new TrackingNotice(
                            TrackingNoticeType.SessionCompleted,
                            active.Game,
                            endedAtUtc,
                            session.Duration,
                            endReason));
                    }
                }

                await _openSessions.DeleteAsync(active.SessionId, cancellationToken);
            }

            _activeGames.Clear();
            _processToGame.Clear();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task PersistActiveCheckpointsAsync(
        DateTimeOffset checkpointAtUtc,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var active in _activeGames.Values)
            {
                await _openSessions.UpsertAsync(
                    CheckpointFor(active, Max(checkpointAtUtc, active.StartedAtUtc)),
                    cancellationToken);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static OpenSessionCheckpoint CheckpointFor(
        ActiveGame active,
        DateTimeOffset checkpointAtUtc) =>
        new(
            active.SessionId,
            active.Game.Id,
            active.StartedAtUtc,
            checkpointAtUtc,
            active.CaptureMethod);

    private static bool IsStart(ProcessObservationType type) =>
        type is ProcessObservationType.Started
            or ProcessObservationType.ReconciledStart
            or ProcessObservationType.InitialSnapshot;

    private static CaptureMethod CaptureMethodFor(ProcessObservationType type) => type switch
    {
        ProcessObservationType.Started => CaptureMethod.Wmi,
        ProcessObservationType.Stopped => CaptureMethod.Wmi,
        ProcessObservationType.InitialSnapshot => CaptureMethod.InitialSnapshot,
        _ => CaptureMethod.Reconciliation
    };

    private static CaptureMethod LessPrecise(CaptureMethod left, CaptureMethod right)
    {
        if (left is CaptureMethod.InitialSnapshot || right is CaptureMethod.InitialSnapshot)
        {
            return CaptureMethod.InitialSnapshot;
        }

        if (left is CaptureMethod.Reconciliation || right is CaptureMethod.Reconciliation)
        {
            return CaptureMethod.Reconciliation;
        }

        if (left is CaptureMethod.Wmi || right is CaptureMethod.Wmi)
        {
            return CaptureMethod.Wmi;
        }

        return CaptureMethod.Etw;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left.ToUniversalTime() : right.ToUniversalTime();

    private sealed class ActiveGame
    {
        public Guid SessionId { get; }
        public TrackedGame Game { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public HashSet<int> ProcessIds { get; } = new();
        public CaptureMethod CaptureMethod { get; set; }

        public ActiveGame(
            Guid sessionId,
            TrackedGame game,
            DateTimeOffset startedAtUtc,
            CaptureMethod captureMethod)
        {
            SessionId = sessionId;
            Game = game;
            StartedAtUtc = startedAtUtc.ToUniversalTime();
            CaptureMethod = captureMethod;
        }
    }
}
