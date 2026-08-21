using GameHours.Core.Domain;
using GameHours.Windows.Achievements;

namespace GameHours.Desktop;

public sealed record DesktopAchievementUnlocked(
    Guid GameId,
    string GameTitle,
    StoredAchievement Achievement);

/// <summary>
/// Observes achievements only while GameHours has a measured session for the game.
/// It fingerprints the concrete state file cheaply once per second and performs a full
/// local re-read only when that file changes or on a low-frequency discovery fallback.
/// </summary>
internal sealed class ActiveAchievementMonitor : IAsyncDisposable
{
    private sealed class GameWatch
    {
        public GameWatch(
            Guid gameId,
            string gameTitle,
            string executablePath,
            DateTimeOffset sessionStartedAtUtc,
            CancellationTokenSource cancellation)
        {
            GameId = gameId;
            GameTitle = gameTitle;
            ExecutablePath = executablePath;
            Gate = new AchievementSessionNotificationGate(sessionStartedAtUtc);
            Cancellation = cancellation;
        }

        public Guid GameId { get; }
        public string GameTitle { get; }
        public string ExecutablePath { get; }
        public AchievementSessionNotificationGate Gate { get; }
        public CancellationTokenSource Cancellation { get; }
        public Task MonitorTask { get; set; } = Task.CompletedTask;
    }

    private readonly record struct StateFileStamp(
        string? FullPath,
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc);

    private readonly DesktopAchievementCoordinator _coordinator;
    private readonly CancellationToken _lifetimeToken;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _fallbackInterval;
    private readonly TimeSpan _sourceDiscoveryInterval;
    private readonly TimeSpan _finalFlushDelay;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, GameWatch> _active = new();
    private bool _disposed;

    public ActiveAchievementMonitor(
        DesktopAchievementCoordinator coordinator,
        CancellationToken lifetimeToken,
        TimeSpan? pollInterval = null,
        TimeSpan? fallbackInterval = null,
        TimeSpan? sourceDiscoveryInterval = null,
        TimeSpan? finalFlushDelay = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _lifetimeToken = lifetimeToken;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _fallbackInterval = fallbackInterval ?? TimeSpan.FromSeconds(15);
        _sourceDiscoveryInterval = sourceDiscoveryInterval ?? TimeSpan.FromSeconds(5);
        _finalFlushDelay = finalFlushDelay ?? TimeSpan.FromMilliseconds(450);

        if (_pollInterval <= TimeSpan.Zero ||
            _fallbackInterval <= TimeSpan.Zero ||
            _sourceDiscoveryInterval <= TimeSpan.Zero ||
            _finalFlushDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
    }

    public event Action<DesktopAchievementUnlocked>? AchievementUnlocked;

    public void Start(
        Guid gameId,
        string gameTitle,
        string executablePath,
        DateTimeOffset sessionStartedAtUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        var watch = new GameWatch(
            gameId,
            gameTitle.Trim(),
            Path.GetFullPath(executablePath),
            sessionStartedAtUtc.ToUniversalTime(),
            cancellation);

        GameWatch? previous = null;
        lock (_gate)
        {
            if (_active.Remove(gameId, out var existing))
            {
                previous = existing;
            }

            _active[gameId] = watch;
            watch.MonitorTask = RunAsync(watch, cancellation.Token);
        }

        if (previous is not null)
        {
            previous.Cancellation.Cancel();
            _ = DisposeWatchAfterCompletionAsync(previous);
        }
    }

    public async Task StopAsync(Guid gameId)
    {
        GameWatch? watch;
        lock (_gate)
        {
            _active.Remove(gameId, out watch);
        }

        if (watch is null)
        {
            return;
        }

        watch.Cancellation.Cancel();
        await AwaitMonitorTaskAsync(watch).ConfigureAwait(false);

        // Some emulators flush achievements only when the process is closing, and the file
        // replacement can finish a fraction of a second after the process-exit observation.
        // Reconcile immediately and once more after a short delay. The session gate keeps this
        // idempotent and prevents duplicate notifications.
        await TryObserveAfterStopAsync(watch).ConfigureAwait(false);
        if (_finalFlushDelay > TimeSpan.Zero)
        {
            await Task.Delay(_finalFlushDelay).ConfigureAwait(false);
            await TryObserveAfterStopAsync(watch).ConfigureAwait(false);
        }

        watch.Cancellation.Dispose();
    }

    private async Task RunAsync(GameWatch watch, CancellationToken cancellationToken)
    {
        LocalAchievementObservationResult? observation = null;
        try
        {
            observation = await ObserveAndPublishAsync(watch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            // Achievement monitoring must never interfere with playtime tracking.
        }

        var statePath = observation?.Snapshot.StatePath;
        var stamp = ReadStamp(statePath);
        var nextFullReadAt = DateTimeOffset.UtcNow + FullReadInterval(statePath);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            var currentStamp = ReadStamp(statePath);
            var stateChanged = currentStamp != stamp;
            if (!stateChanged && now < nextFullReadAt)
            {
                continue;
            }

            try
            {
                observation = await ObserveAndPublishAsync(watch, cancellationToken).ConfigureAwait(false);
                if (observation is not null)
                {
                    statePath = observation.Snapshot.StatePath;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A transient read/lock error is retried on the next fingerprint/fallback pass.
            }

            stamp = ReadStamp(statePath);
            nextFullReadAt = DateTimeOffset.UtcNow + FullReadInterval(statePath);
        }
    }

    private async Task<LocalAchievementObservationResult?> ObserveAndPublishAsync(
        GameWatch watch,
        CancellationToken cancellationToken)
    {
        var result = await _coordinator.ObserveAsync(
            watch.GameId,
            watch.ExecutablePath,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var observedAtUtc = DateTimeOffset.UtcNow;
        foreach (var achievement in watch.Gate.AcceptReadableObservation(
                     observedAtUtc,
                     result.IsBaseline,
                     result.NotificationCandidates))
        {
            AchievementUnlocked?.Invoke(new DesktopAchievementUnlocked(
                watch.GameId,
                watch.GameTitle,
                achievement));
        }

        return result;
    }

    private async Task TryObserveAfterStopAsync(GameWatch watch)
    {
        try
        {
            await ObserveAndPublishAsync(watch, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Exit-flush reconciliation is best effort and must never delay session shutdown
            // beyond the bounded retry above or affect playtime persistence.
        }
    }

    private TimeSpan FullReadInterval(string? statePath) =>
        string.IsNullOrWhiteSpace(statePath)
            ? _sourceDiscoveryInterval
            : _fallbackInterval;

    private static StateFileStamp ReadStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new StateFileStamp(null, false, 0, DateTime.MinValue);
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            info.Refresh();
            return info.Exists
                ? new StateFileStamp(fullPath, true, info.Length, info.LastWriteTimeUtc)
                : new StateFileStamp(fullPath, false, 0, DateTime.MinValue);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            PathTooLongException or NotSupportedException)
        {
            return new StateFileStamp(path, false, 0, DateTime.MinValue);
        }
    }

    private static async Task AwaitMonitorTaskAsync(GameWatch watch)
    {
        try
        {
            await watch.MonitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (watch.Cancellation.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private static async Task DisposeWatchAfterCompletionAsync(GameWatch watch)
    {
        await AwaitMonitorTaskAsync(watch).ConfigureAwait(false);
        watch.Cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GameWatch[] active;
        lock (_gate)
        {
            active = _active.Values.ToArray();
            _active.Clear();
        }

        foreach (var watch in active)
        {
            watch.Cancellation.Cancel();
        }

        foreach (var watch in active)
        {
            await AwaitMonitorTaskAsync(watch).ConfigureAwait(false);
            watch.Cancellation.Dispose();
        }
    }
}
