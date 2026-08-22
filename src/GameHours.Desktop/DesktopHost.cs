using System.IO;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Tracking;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Processes;

namespace GameHours.Desktop;

public sealed record DesktopActivityRow(Guid SessionId, Guid GameId, string GameTitle, DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, TimeSpan Duration, string? EndReason);
public enum DesktopTimelineKind { Session = 1, AchievementUnlocked = 2, AchievementCompleted = 3 }
public sealed record DesktopTimelineRow(Guid GameId, string GameTitle, DateTimeOffset OccurredAtUtc, DesktopTimelineKind Kind, TimeSpan? Duration = null, string? EndReason = null, string? AchievementApiName = null, string? AchievementDisplayName = null, bool IsObservedTimeFallback = false);
public sealed record DesktopGameRow(Guid GameId, string Title, TimeSpan TotalPlaytime, TimeSpan MeasuredPlaytime, TimeSpan EstimatedPlaytime, DateTimeOffset? FirstActivityAtUtc, DateTimeOffset? LastActivityAtUtc, DateTimeOffset? FirstMeasuredSessionAtUtc, DateTimeOffset? LastMeasuredSessionAtUtc, int MeasuredSessionCount, string? ExecutablePath, IReadOnlyList<DesktopActivityRow> RecentSessions);
public sealed record DesktopStatus(bool IsTracking, string StatusText, string? ActiveGameTitle, DateTimeOffset? ActiveGameStartedAtUtc, IReadOnlyList<DesktopGameRow> Games, IReadOnlyList<DesktopTimelineRow> RecentActivity);

public sealed class DesktopHost : IAsyncDisposable
{
    private sealed record ActiveDesktopGame(string Title, DateTimeOffset StartedAtUtc);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _activeGate = new();
    private readonly Dictionary<Guid, ActiveDesktopGame> _activeGames = new();
    private int _refreshRequested;
    private int _refreshRunning;

    private GameHoursDatabase? _database;
    private SqliteGameRepository? _games;
    private SqliteExecutableMappingRepository? _mappings;
    private SqliteSessionRepository? _sessions;
    private SqliteHistoricalEvidenceRepository? _historicalEvidence;
    private SqliteAchievementActivityRepository? _achievementActivity;
    private SqliteGameCandidateRepository? _candidates;
    private IOpenSessionRepository? _openSessions;
    private ITrackingStateRepository? _trackingState;
    private CandidateRecordingGameResolver? _resolver;
    private ActiveAchievementMonitor? _achievementMonitor;
    private GameSessionEngine? _engine;
    private Task? _trackingTask;
    private IReadOnlyList<DesktopGameRow> _library = Array.Empty<DesktopGameRow>();
    private IReadOnlyList<DesktopTimelineRow> _recentActivity = Array.Empty<DesktopTimelineRow>();
    private DesktopStatus _currentStatus = new(false, "Preparando…", null, null, Array.Empty<DesktopGameRow>(), Array.Empty<DesktopTimelineRow>());
    private bool _disposed;

    public event Action<DesktopStatus>? StatusChanged;
    public event Action<DesktopAchievementUnlocked>? AchievementUnlocked;
    public event Action? CandidatesChanged;

    public string DatabasePath => _database?.DatabasePath ?? string.Empty;
    public DesktopStatus CurrentStatus => _currentStatus;
    private bool IsTrackerRunning => _trackingTask is { IsCompleted: false };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameHours");
        var databasePath = Path.Combine(dataDirectory, "gamehours.db");
        _database = new GameHoursDatabase(databasePath);
        await _database.InitializeAsync(cancellationToken);

        var achievementCoordinator = new DesktopAchievementCoordinator(databasePath);
        _achievementMonitor = new ActiveAchievementMonitor(achievementCoordinator, _lifetime.Token);
        _achievementMonitor.AchievementUnlocked += HandleAchievementUnlocked;

        var discovery = new InstalledGameDiscoveryService(new IInstalledGameSource[]
        {
            new SteamInstalledGameSource(),
            new EpicInstalledGameSource(),
            new GogInstalledGameSource()
        });
        var installedGames = await discovery.DiscoverAsync(cancellationToken);

        _games = new SqliteGameRepository(_database);
        _mappings = new SqliteExecutableMappingRepository(_database);
        _sessions = new SqliteSessionRepository(_database);
        _achievementActivity = new SqliteAchievementActivityRepository(_database);
        _candidates = new SqliteGameCandidateRepository(_database);
        _trackingState = new SqliteTrackingStateRepository(_database);
        _openSessions = new SqliteOpenSessionRepository(_database);
        _historicalEvidence = new SqliteHistoricalEvidenceRepository(_database, _trackingState, _sessions);

        var learning = new LearningGameResolver(new WindowsGameResolver(installedGames), _mappings, _games);
        _resolver = new CandidateRecordingGameResolver(learning, _candidates);
        _resolver.CandidateRecorded += HandleCandidateRecorded;

        await ReloadLocalDataAsync(cancellationToken);
        PublishStatus(false, "Preparado para monitorizar");
    }

    public Task StartAsync()
    {
        ThrowIfDisposed();
        if (_resolver is null || _games is null || _sessions is null || _openSessions is null || _trackingState is null)
            throw new InvalidOperationException("DesktopHost must be initialized before tracking starts.");
        if (IsTrackerRunning) return Task.CompletedTask;

        if (_engine is not null) _engine.Notice -= HandleTrackingNotice;
        var monitor = new HybridWindowsProcessMonitor(new WindowsProcessSnapshotProvider(), TimeSpan.FromSeconds(1));
        _engine = new GameSessionEngine(monitor, _resolver, _games, _sessions, _openSessions, _trackingState);
        _engine.Notice += HandleTrackingNotice;
        _trackingTask = RunTrackerAsync(_engine, _lifetime.Token);
        PublishStatus(true, "Monitorizando juegos");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var task = _trackingTask;
        if (task is null) return;
        if (task.IsCompleted)
        {
            if (ReferenceEquals(_trackingTask, task)) _trackingTask = null;
            PublishStatus(false, "Detenido");
            return;
        }

        PublishStatus(true, "Guardando sesión activa…");
        GracefulShutdownSignal.Request();
        try { await task.WaitAsync(cancellationToken); }
        finally
        {
            if (ReferenceEquals(_trackingTask, task)) _trackingTask = null;
            PublishStatus(false, "Detenido");
        }
    }

    public async Task RefreshLibraryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await ReloadLocalDataAsync(cancellationToken);
        PublishStatus(IsTrackerRunning, IsTrackerRunning ? "Monitorizando juegos" : "Detenido");
    }

    public Task<int> GetPendingCandidateCountAsync(CancellationToken cancellationToken = default) =>
        _candidates?.GetPendingCountAsync(cancellationToken) ?? Task.FromResult(0);

    private async Task RunTrackerAsync(GameSessionEngine engine, CancellationToken cancellationToken)
    {
        try
        {
            await engine.RunAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested) PublishStatus(false, "Tracker detenido");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await ClearActiveGamesAsync();
            PublishStatus(false, $"Error del tracker: {exception.Message}");
        }
    }

    private void HandleTrackingNotice(TrackingNotice notice)
    {
        switch (notice.Type)
        {
            case TrackingNoticeType.SessionStarted:
                lock (_activeGate) _activeGames[notice.Game.Id] = new(notice.Game.Title, notice.AtUtc.ToUniversalTime());
                PublishStatus(true, $"Jugando a {notice.Game.Title}");
                _ = StartAchievementMonitoringAsync(notice);
                break;
            case TrackingNoticeType.SessionCompleted:
                lock (_activeGate) _activeGames.Remove(notice.Game.Id);
                _ = StopAchievementMonitoringAsync(notice.Game.Id);
                PublishStatus(true, "Monitorizando juegos");
                QueueLibraryRefresh();
                break;
            case TrackingNoticeType.SessionRecovered:
                QueueLibraryRefresh();
                break;
        }
    }

    private void HandleCandidateRecorded() => CandidatesChanged?.Invoke();

    private async Task StartAchievementMonitoringAsync(TrackingNotice notice)
    {
        if (_achievementMonitor is null || _mappings is null) return;
        try
        {
            var mappings = await _mappings.GetForGameAsync(notice.Game.Id, includeHelpers: false, _lifetime.Token);
            var executablePath = mappings.Select(item => item.ExecutablePath).FirstOrDefault(File.Exists)
                ?? mappings.Select(item => item.ExecutablePath).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(executablePath)) return;
            lock (_activeGate)
            {
                if (!_activeGames.TryGetValue(notice.Game.Id, out var active) || active.StartedAtUtc != notice.AtUtc.ToUniversalTime()) return;
            }
            _achievementMonitor.Start(notice.Game.Id, notice.Game.Title, executablePath, notice.AtUtc);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch { }
    }

    private async Task StopAchievementMonitoringAsync(Guid gameId)
    {
        if (_achievementMonitor is null) return;
        try { await _achievementMonitor.StopAsync(gameId); }
        catch { }
    }

    private async Task ClearActiveGamesAsync()
    {
        Guid[] gameIds;
        lock (_activeGate)
        {
            gameIds = _activeGames.Keys.ToArray();
            _activeGames.Clear();
        }
        foreach (var gameId in gameIds) await StopAchievementMonitoringAsync(gameId);
    }

    private void HandleAchievementUnlocked(DesktopAchievementUnlocked notice)
    {
        AchievementUnlocked?.Invoke(notice);
        QueueLibraryRefresh();
    }

    private void QueueLibraryRefresh()
    {
        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) == 0) _ = DrainLibraryRefreshAsync();
    }

    private async Task DrainLibraryRefreshAsync()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _refreshRequested, 0);
                try { await RefreshLibraryAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch { }
            }
            while (Volatile.Read(ref _refreshRequested) != 0);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
            if (Volatile.Read(ref _refreshRequested) != 0) QueueLibraryRefresh();
        }
    }

    private async Task ReloadLocalDataAsync(CancellationToken cancellationToken)
    {
        if (_games is null || _mappings is null || _sessions is null || _historicalEvidence is null || _achievementActivity is null)
        {
            _library = Array.Empty<DesktopGameRow>();
            _recentActivity = Array.Empty<DesktopTimelineRow>();
            return;
        }

        var gamesTask = _games.GetAllAsync(cancellationToken);
        var sessionsTask = _sessions.GetAllAsync(cancellationToken: cancellationToken);
        var evidenceTask = _historicalEvidence.GetAllAsync(cancellationToken);
        var mappingsTask = _mappings.GetAllAsync(includeHelpers: false, cancellationToken);
        var unlocksTask = _achievementActivity.GetRecentUnlocksAsync(50, cancellationToken: cancellationToken);
        var completionsTask = _achievementActivity.GetRecentCompletionMilestonesAsync(50, cancellationToken: cancellationToken);
        await Task.WhenAll(gamesTask, sessionsTask, evidenceTask, mappingsTask, unlocksTask, completionsTask);

        var games = await gamesTask;
        var sessionsByGame = (await sessionsTask).ToLookup(item => item.GameId);
        var evidenceByGame = (await evidenceTask).ToLookup(item => item.GameId);
        var mappingsByGame = (await mappingsTask).ToLookup(item => item.GameId);
        var rows = new List<DesktopGameRow>(games.Count);
        var sessionsForTimeline = new List<DesktopActivityRow>();

        foreach (var game in games)
        {
            var sessions = sessionsByGame[game.Id].ToArray();
            var evidence = evidenceByGame[game.Id].ToArray();
            var mappings = mappingsByGame[game.Id].ToArray();
            var measuredTicks = sessions.Aggregate(0L, (total, item) => checked(total + item.Duration.Ticks));
            var estimatedTicks = evidence.Aggregate(0L, (total, item) => checked(total + item.Duration.Ticks));
            DateTimeOffset? firstMeasured = sessions.Length > 0 ? sessions.Min(item => item.StartedAtUtc) : null;
            DateTimeOffset? lastMeasured = sessions.Length > 0 ? sessions.Max(item => item.EndedAtUtc) : null;
            DateTimeOffset? firstActivity = firstMeasured;
            DateTimeOffset? lastActivity = lastMeasured;
            if (evidence.Length > 0)
            {
                var firstEvidence = evidence.Min(item => item.PeriodStartUtc);
                var lastEvidence = evidence.Max(item => item.PeriodEndUtc);
                if (firstActivity is null || firstEvidence < firstActivity) firstActivity = firstEvidence;
                if (lastActivity is null || lastEvidence > lastActivity) lastActivity = lastEvidence;
            }

            var executablePath = mappings.Select(item => item.ExecutablePath).FirstOrDefault(File.Exists)
                ?? mappings.Select(item => item.ExecutablePath).FirstOrDefault();
            var activity = sessions
                .Select(item => new DesktopActivityRow(item.Id, game.Id, game.Title, item.StartedAtUtc, item.EndedAtUtc, item.Duration, item.EndReason))
                .OrderByDescending(item => item.EndedAtUtc)
                .ToArray();

            rows.Add(new DesktopGameRow(
                game.Id,
                game.Title,
                TimeSpan.FromTicks(checked(measuredTicks + estimatedTicks)),
                TimeSpan.FromTicks(measuredTicks),
                TimeSpan.FromTicks(estimatedTicks),
                firstActivity,
                lastActivity,
                firstMeasured,
                lastMeasured,
                sessions.Length,
                executablePath,
                activity.Take(20).ToArray()));
            sessionsForTimeline.AddRange(activity);
        }

        _library = rows.OrderByDescending(item => item.TotalPlaytime).ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        _recentActivity = sessionsForTimeline
            .Select(item => new DesktopTimelineRow(item.GameId, item.GameTitle, item.EndedAtUtc, DesktopTimelineKind.Session, item.Duration, item.EndReason))
            .Concat((await unlocksTask).Select(item => new DesktopTimelineRow(
                item.GameId,
                item.GameTitle,
                item.OccurredAtUtc,
                DesktopTimelineKind.AchievementUnlocked,
                AchievementApiName: item.ApiName,
                AchievementDisplayName: AchievementPresentation.TimelineText(item.DisplayName, item.ApiName, item.Description),
                IsObservedTimeFallback: item.IsObservedTimeFallback)))
            .Concat((await completionsTask).Select(item => new DesktopTimelineRow(
                item.GameId,
                item.GameTitle,
                item.CompletedAtUtc,
                DesktopTimelineKind.AchievementCompleted,
                AchievementDisplayName: "100 % completado",
                IsObservedTimeFallback: item.IsObservedTimeFallback)))
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.Kind)
            .Take(50)
            .ToArray();
    }

    private void PublishStatus(bool isTracking, string statusText)
    {
        ActiveDesktopGame? active;
        lock (_activeGate) active = _activeGames.Values.OrderBy(item => item.StartedAtUtc).FirstOrDefault();
        _currentStatus = new(isTracking, statusText, active?.Title, active?.StartedAtUtc, _library, _recentActivity);
        StatusChanged?.Invoke(_currentStatus);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync(); }
        finally
        {
            if (_engine is not null) _engine.Notice -= HandleTrackingNotice;
            if (_resolver is not null) _resolver.CandidateRecorded -= HandleCandidateRecorded;
            if (_achievementMonitor is not null)
            {
                _achievementMonitor.AchievementUnlocked -= HandleAchievementUnlocked;
                await _achievementMonitor.DisposeAsync();
            }
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
