using GameHours.Core.Domain;
using GameHours.Windows.Achievements;
using GameHours.Windows.IO;

namespace GameHours.Desktop;

public sealed record DesktopAchievementUnlocked(
    Guid GameId,
    string GameTitle,
    StoredAchievement Achievement);

/// <summary>
/// Observes achievements only while GameHours has a measured session for the game. Once a
/// concrete state file is known, Windows filesystem notifications wake the monitor only when
/// that exact file changes. A low-frequency fallback remains because filesystem events are not
/// guaranteed delivery; source discovery stays periodic only while there is no file to watch.
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

    private readonly DesktopAchievementCoordinator _coordinator;
    private readonly CancellationToken _lifetimeToken;
    private readonly TimeSpan _fallbackInterval;
    private readonly TimeSpan _sourceDiscoveryInterval;
    private readonly TimeSpan _eventSettleDelay;
    private readonly TimeSpan _finalFlushDelay;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, GameWatch> _active = new();
    private bool _disposed;

    public ActiveAchievementMonitor(
        DesktopAchievementCoordinator coordinator,
        CancellationToken lifetimeToken,
        TimeSpan? fallbackInterval = null,
        TimeSpan? sourceDiscoveryInterval = null,
        TimeSpan? eventSettleDelay = null,
        TimeSpan? finalFlushDelay = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _lifetimeToken = lifetimeToken;
        _fallbackInterval = fallbackInterval ?? TimeSpan.FromSeconds(30);
        _sourceDiscoveryInterval = sourceDiscoveryInterval ?? TimeSpan.FromSeconds(5);
        _eventSettleDelay = eventSettleDelay ?? TimeSpan.FromMilliseconds(150);
        _finalFlushDelay = finalFlushDelay ?? TimeSpan.FromMilliseconds(450);

        if (_fallbackInterval <= TimeSpan.Zero ||
            _sourceDiscoveryInterval <= TimeSpan.Zero ||
            _eventSettleDelay < TimeSpan.Zero ||
            _finalFlushDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackInterval));
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

        var statePath = NormalizeStatePath(observation?.Snapshot.StatePath);
        TargetFileChangeWatcher? fileWatcher = TargetFileChangeWatcher.TryCreate(statePath);
        if (fileWatcher is not null)
        {
            statePath = fileWatcher.FullPath;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TargetFileWakeReason wakeReason;
                try
                {
                    wakeReason = await WaitForWorkAsync(fileWatcher, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (wakeReason == TargetFileWakeReason.Changed && _eventSettleDelay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(_eventSettleDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }

                if (wakeReason == TargetFileWakeReason.WatcherFaulted)
                {
                    fileWatcher?.Dispose();
                    fileWatcher = null;
                }

                try
                {
                    observation = await ObserveAndPublishAsync(watch, cancellationToken).ConfigureAwait(false);
                    if (observation is not null)
                    {
                        var observedStatePath = NormalizeStatePath(observation.Snapshot.StatePath);
                        if (observedStatePath is not null && !PathsEqual(statePath, observedStatePath))
                        {
                            statePath = observedStatePath;
                            fileWatcher?.Dispose();
                            fileWatcher = null;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // A transient read/lock error is retried by the next file event or fallback.
                }

                if (fileWatcher is null && statePath is not null)
                {
                    fileWatcher = TargetFileChangeWatcher.TryCreate(statePath);
                    if (fileWatcher is not null)
                    {
                        statePath = fileWatcher.FullPath;
                    }
                }
            }
        }
        finally
        {
            fileWatcher?.Dispose();
        }
    }

    private async Task<TargetFileWakeReason> WaitForWorkAsync(
        TargetFileChangeWatcher? fileWatcher,
        CancellationToken cancellationToken)
    {
        if (fileWatcher is not null)
        {
            return await fileWatcher.WaitAsync(_fallbackInterval, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(_sourceDiscoveryInterval, cancellationToken).ConfigureAwait(false);
        return TargetFileWakeReason.Fallback;
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

    private static string? NormalizeStatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
