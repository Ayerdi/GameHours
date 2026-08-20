using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Core.Tracking;

public enum TrackingNoticeType
{
    SessionStarted = 1,
    SessionCompleted = 2
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
    private readonly ITrackingStateRepository _trackingState;
    private readonly double _minimumResolutionConfidence;
    private readonly TimeProvider _timeProvider;

    private readonly Dictionary<int, Guid> _processToGame = new();
    private readonly Dictionary<Guid, ActiveGame> _activeGames = new();

    public event Action<TrackingNotice>? Notice;

    public GameSessionEngine(
        IProcessMonitor monitor,
        IGameResolver resolver,
        IGameRepository games,
        ISessionRepository sessions,
        ITrackingStateRepository trackingState,
        double minimumResolutionConfidence = 0.80,
        TimeProvider? timeProvider = null)
    {
        if (minimumResolutionConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumResolutionConfidence));
        }

        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _trackingState = trackingState ?? throw new ArgumentNullException(nameof(trackingState));
        _minimumResolutionConfidence = minimumResolutionConfidence;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var runStartedAt = _timeProvider.GetUtcNow();
        var cutover = await _trackingState.GetOrSetTrackingStartedAtAsync(
            runStartedAt,
            cancellationToken);

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

        var game = resolution.Game;
        _processToGame[observation.ProcessId] = game.Id;

        if (!_activeGames.TryGetValue(game.Id, out var active))
        {
            var startedAt = observation.Type is ProcessObservationType.InitialSnapshot
                ? Max(runStartedAt, cutover)
                : Max(observation.OccurredAtUtc, cutover);

            active = new ActiveGame(
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
    }

    private async Task HandleStopAsync(
        ProcessObservation observation,
        CancellationToken cancellationToken)
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
            return;
        }

        _activeGames.Remove(gameId);
        var endedAt = observation.OccurredAtUtc.ToUniversalTime();
        if (endedAt <= active.StartedAtUtc)
        {
            return;
        }

        var session = new PlaySession(
            Guid.NewGuid(),
            gameId,
            active.StartedAtUtc,
            endedAt,
            active.CaptureMethod,
            Confidence.High,
            observation.Type.ToString());

        await _sessions.AddAsync(session, cancellationToken);

        Notice?.Invoke(new TrackingNotice(
            TrackingNoticeType.SessionCompleted,
            active.Game,
            endedAt,
            session.Duration,
            observation.Type.ToString()));
    }

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
        public TrackedGame Game { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public HashSet<int> ProcessIds { get; } = new();
        public CaptureMethod CaptureMethod { get; set; }

        public ActiveGame(TrackedGame game, DateTimeOffset startedAtUtc, CaptureMethod captureMethod)
        {
            Game = game;
            StartedAtUtc = startedAtUtc.ToUniversalTime();
            CaptureMethod = captureMethod;
        }
    }
}
