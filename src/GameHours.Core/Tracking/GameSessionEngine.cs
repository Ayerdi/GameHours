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
    private readonly IUserInteractionStateProvider? _interactionStateProvider;
    private readonly ISessionActivityRepository? _sessionActivity;
    private readonly double _minimumResolutionConfidence;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _checkpointInterval;
    private readonly TimeSpan _activitySampleInterval;
    private readonly TimeSpan _idleThreshold;
    private readonly TimeSpan _maxActivitySampleGap;
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
        TimeSpan? checkpointInterval = null,
        IUserInteractionStateProvider? interactionStateProvider = null,
        ISessionActivityRepository? sessionActivity = null,
        TimeSpan? activitySampleInterval = null,
        TimeSpan? idleThreshold = null)
    {
        if (minimumResolutionConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumResolutionConfidence));
        }

        if ((interactionStateProvider is null) != (sessionActivity is null))
        {
            throw new ArgumentException(
                "Interaction state provider and session activity repository must be configured together.");
        }

        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _openSessions = openSessions ?? throw new ArgumentNullException(nameof(openSessions));
        _trackingState = trackingState ?? throw new ArgumentNullException(nameof(trackingState));
        _interactionStateProvider = interactionStateProvider;
        _sessionActivity = sessionActivity;
        _minimumResolutionConfidence = minimumResolutionConfidence;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkpointInterval = checkpointInterval ?? TimeSpan.FromSeconds(5);
        _activitySampleInterval = activitySampleInterval ?? TimeSpan.FromSeconds(1);
        _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(5);

        if (_checkpointInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointInterval));
        }

        if (_activitySampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(activitySampleInterval));
        }

        if (_idleThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleThreshold));
        }

        _maxActivitySampleGap = TimeSpan.FromTicks(
            checked(_activitySampleInterval.Ticks * 3));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var shutdownCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gracefulSignalRequested = 0;

        void OnGracefulShutdownRequested()
        {
            Interlocked.Exchange(ref gracefulSignalRequested, 1);
            try
            {
                shutdownCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        GracefulShutdownSignal.Requested += OnGracefulShutdownRequested;
        try
        {
            var runStartedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            await RecoverInterruptedSessionsAsync(shutdownCancellation.Token);

            var cutover = await _trackingState.GetOrSetTrackingStartedAtAsync(
                runStartedAt,
                shutdownCancellation.Token);

            using var checkpointCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                shutdownCancellation.Token);
            using var activityCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                shutdownCancellation.Token);
            var checkpointTask = CheckpointLoopAsync(checkpointCancellation.Token);
            var activityTask = ActivityLoopAsync(activityCancellation.Token);

            try
            {
                await foreach (var observation in _monitor.ObserveAsync(shutdownCancellation.Token))
                {
                    if (IsStart(observation.Type))
                    {
                        await HandleStartAsync(
                            observation,
                            cutover,
                            runStartedAt,
                            shutdownCancellation.Token);
                    }
                    else
                    {
                        await HandleStopAsync(observation, shutdownCancellation.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (
                Volatile.Read(ref gracefulSignalRequested) != 0 &&
                !cancellationToken.IsCancellationRequested)
            {
                // A host-level graceful signal is internal to the engine. Finish cleanup below
                // and return normally so callers do not need to know which host initiated it.
            }
            finally
            {
                // Stop observation before persisting/finalizing so no activity sample races with
                // the final session boundary.
                activityCancellation.Cancel();
                try
                {
                    await activityTask;
                }
                catch (OperationCanceledException) when (activityCancellation.IsCancellationRequested)
                {
                }

                checkpointCancellation.Cancel();
                try
                {
                    await checkpointTask;
                }
                catch (OperationCanceledException) when (checkpointCancellation.IsCancellationRequested)
                {
                }

                var stoppedAt = _timeProvider.GetUtcNow().ToUniversalTime();
                if (shutdownCancellation.IsCancellationRequested)
                {
                    // Intentional cancellation is not a crash: close every observed segment at
                    // the actual shutdown boundary and remove its durable checkpoint.
                    await FinalizeActiveSessionsAsync(
                        stoppedAt,
                        "GracefulShutdown",
                        CancellationToken.None);
                }
                else
                {
                    // Natural/exceptional monitor termination is not known to be intentional.
                    // Preserve conservative crash recovery semantics instead of inventing an
                    // exact end boundary that the caller did not request.
                    await PersistActiveCheckpointsAsync(stoppedAt, CancellationToken.None);
                }
            }
        }
        finally
        {
            GracefulShutdownSignal.Requested -= OnGracefulShutdownRequested;
        }
    }

    private async Task RecoverInterruptedSessionsAsync(CancellationToken cancellationToken)
    {
        var interrupted = await _openSessions.GetAllAsync(cancellationToken);
        foreach (var checkpoint in interrupted)
        {
            var inserted = false;
            var recoverable = checkpoint.LastCheckpointAtUtc > checkpoint.StartedAtUtc;
            if (recoverable)
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
                await FinalizeRecoveredActivityAsync(checkpoint, session.Duration, cancellationToken);
            }
            else if (_sessionActivity is not null)
            {
                await _sessionActivity.DeleteAsync(checkpoint.SessionId, cancellationToken);
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

    private async Task FinalizeRecoveredActivityAsync(
        OpenSessionCheckpoint checkpoint,
        TimeSpan recoveredDuration,
        CancellationToken cancellationToken)
    {
        if (_sessionActivity is null) return;

        var existing = await _sessionActivity.GetBySessionIdAsync(
            checkpoint.SessionId,
            cancellationToken);
        if (existing is null) return;

        var focused = existing.FocusedDuration <= recoveredDuration
            ? existing.FocusedDuration
            : recoveredDuration;
        var active = existing.ActiveDuration <= focused
            ? existing.ActiveDuration
            : focused;

        await _sessionActivity.UpsertAsync(
            existing with
            {
                FocusedDuration = focused,
                ActiveDuration = active,
                IsFinalized = true,
                UpdatedAtUtc = checkpoint.LastCheckpointAtUtc.ToUniversalTime()
            },
            cancellationToken);
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
            await PersistCheckpointForAsync(
                active,
                Max(observation.OccurredAtUtc, active.StartedAtUtc),
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
                await PersistCheckpointForAsync(
                    active,
                    Max(observation.OccurredAtUtc, active.StartedAtUtc),
                    cancellationToken);
                return;
            }

            _activeGames.Remove(gameId);
            var endedAt = observation.OccurredAtUtc.ToUniversalTime();
            if (endedAt <= active.StartedAtUtc)
            {
                await _openSessions.DeleteAsync(active.SessionId, cancellationToken);
                if (_sessionActivity is not null)
                {
                    await _sessionActivity.DeleteAsync(active.SessionId, cancellationToken);
                }
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
            await PersistActivityForAsync(active, endedAt, isFinalized: true, cancellationToken);

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

    private async Task ActivityLoopAsync(CancellationToken cancellationToken)
    {
        if (_interactionStateProvider is null || _sessionActivity is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(_activitySampleInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!await HasActiveGamesAsync(cancellationToken))
            {
                continue;
            }

            var sampledAt = _timeProvider.GetUtcNow().ToUniversalTime();
            UserInteractionState state;
            try
            {
                state = await _interactionStateProvider.GetStateAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Attention telemetry is deliberately secondary to authoritative session
                // tracking. If Windows interaction observation fails, leave this interval
                // unknown instead of failing or extending the play session itself.
                await ResetActivitySampleBoundaryAsync(sampledAt, cancellationToken);
                continue;
            }

            await AccumulateActivityAsync(state, sampledAt, cancellationToken);
        }
    }

    private async Task<bool> HasActiveGamesAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            return _activeGames.Count > 0;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task AccumulateActivityAsync(
        UserInteractionState state,
        DateTimeOffset sampledAtUtc,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            Guid? focusedGameId = null;
            if (state.ForegroundProcessId is int foregroundProcessId &&
                _processToGame.TryGetValue(foregroundProcessId, out var resolvedGameId))
            {
                focusedGameId = resolvedGameId;
            }

            foreach (var active in _activeGames.Values)
            {
                var elapsed = sampledAtUtc - active.LastActivitySampleAtUtc;
                active.LastActivitySampleAtUtc = sampledAtUtc;

                var delta = SessionActivityPolicy.Measure(
                    elapsed,
                    isFocused: focusedGameId == active.Game.Id,
                    state.IdleDuration,
                    _idleThreshold,
                    _maxActivitySampleGap);

                active.FocusedDuration += delta.FocusedDuration;
                active.ActiveDuration += delta.ActiveDuration;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task ResetActivitySampleBoundaryAsync(
        DateTimeOffset sampledAtUtc,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var active in _activeGames.Values)
            {
                active.LastActivitySampleAtUtc = sampledAtUtc;
            }
        }
        finally
        {
            _stateGate.Release();
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
                    await PersistActivityForAsync(active, endedAtUtc, isFinalized: true, cancellationToken);
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
                else if (_sessionActivity is not null)
                {
                    await _sessionActivity.DeleteAsync(active.SessionId, cancellationToken);
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
                await PersistCheckpointForAsync(
                    active,
                    Max(checkpointAtUtc, active.StartedAtUtc),
                    cancellationToken);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private async Task PersistCheckpointForAsync(
        ActiveGame active,
        DateTimeOffset checkpointAtUtc,
        CancellationToken cancellationToken)
    {
        await _openSessions.UpsertAsync(
            CheckpointFor(active, checkpointAtUtc),
            cancellationToken);
        await PersistActivityForAsync(active, checkpointAtUtc, isFinalized: false, cancellationToken);
    }

    private Task PersistActivityForAsync(
        ActiveGame active,
        DateTimeOffset boundaryAtUtc,
        bool isFinalized,
        CancellationToken cancellationToken)
    {
        if (_sessionActivity is null)
        {
            return Task.CompletedTask;
        }

        var sessionDuration = boundaryAtUtc > active.StartedAtUtc
            ? boundaryAtUtc - active.StartedAtUtc
            : TimeSpan.Zero;
        var focused = active.FocusedDuration <= sessionDuration
            ? active.FocusedDuration
            : sessionDuration;
        var activeDuration = active.ActiveDuration <= focused
            ? active.ActiveDuration
            : focused;

        return _sessionActivity.UpsertAsync(
            new SessionActivityMetrics(
                active.SessionId,
                active.Game.Id,
                focused,
                activeDuration,
                _idleThreshold,
                isFinalized,
                boundaryAtUtc.ToUniversalTime()),
            cancellationToken);
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
        public DateTimeOffset LastActivitySampleAtUtc { get; set; }
        public TimeSpan FocusedDuration { get; set; }
        public TimeSpan ActiveDuration { get; set; }

        public ActiveGame(
            Guid sessionId,
            TrackedGame game,
            DateTimeOffset startedAtUtc,
            CaptureMethod captureMethod)
        {
            SessionId = sessionId;
            Game = game;
            StartedAtUtc = startedAtUtc.ToUniversalTime();
            LastActivitySampleAtUtc = StartedAtUtc;
            CaptureMethod = captureMethod;
        }
    }
}
