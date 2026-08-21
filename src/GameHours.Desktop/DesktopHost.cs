using System.IO;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Tracking;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Processes;

namespace GameHours.Desktop;

public sealed record DesktopActivityRow(
    Guid SessionId,
    Guid GameId,
    string GameTitle,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    TimeSpan Duration,
    string? EndReason);

public enum DesktopTimelineKind
{
    Session = 1,
    AchievementUnlocked = 2
}

public sealed record DesktopTimelineRow(
    Guid GameId,
    string GameTitle,
    DateTimeOffset OccurredAtUtc,
    DesktopTimelineKind Kind,
    TimeSpan? Duration = null,
    string? EndReason = null,
    string? AchievementApiName = null,
    string? AchievementDisplayName = null,
    bool IsObservedTimeFallback = false);

public sealed record DesktopGameRow(
    Guid GameId,
    string Title,
    TimeSpan TotalPlaytime,
    TimeSpan MeasuredPlaytime,
    TimeSpan EstimatedPlaytime,
    DateTimeOffset? FirstActivityAtUtc,
    DateTimeOffset? LastActivityAtUtc,
    DateTimeOffset? FirstMeasuredSessionAtUtc,
    DateTimeOffset? LastMeasuredSessionAtUtc,
    int MeasuredSessionCount,
    string? ExecutablePath,
    IReadOnlyList<DesktopActivityRow> RecentSessions);

public sealed record DesktopStatus(
    bool IsTracking,
    string StatusText,
    string? ActiveGameTitle,
    DateTimeOffset? ActiveGameStartedAtUtc,
    IReadOnlyList<DesktopGameRow> Games,
    IReadOnlyList<DesktopTimelineRow> RecentActivity);

public sealed class DesktopHost : IAsyncDisposable
{
    private sealed record ActiveDesktopGame(string Title, DateTimeOffset StartedAtUtc);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _activeGate = new();
    private readonly Dictionary<Guid, ActiveDesktopGame> _activeGames = new();

    private GameHoursDatabase? _database;
    private SqliteGameRepository? _games;
    private SqliteExecutableMappingRepository? _mappings;
    private SqliteSessionRepository? _sessions;
    private SqliteHistoricalEvidenceRepository? _historicalEvidence;
    private SqliteAchievementActivityRepository? _achievementActivity;
    private ActiveAchievementMonitor? _achievementMonitor;
    private GameSessionEngine? _engine;
    private Task? _trackingTask;
    private IReadOnlyList<DesktopGameRow> _library = Array.Empty<DesktopGameRow>();
    private IReadOnlyList<DesktopTimelineRow> _recentActivity = Array.Empty<DesktopTimelineRow>();
    private DesktopStatus _currentStatus = new(
        false,
        "Preparando…",
        null,
        null,
        Array.Empty<DesktopGameRow>(),
        Array.Empty<DesktopTimelineRow>());
    private bool _disposed;

    public event Action<DesktopStatus>? StatusChanged;
    public event Action<DesktopAchievementUnlocked>? AchievementUnlocked;

    public string DatabasePath => _database?.DatabasePath ?? string.Empty;
    public DesktopStatus CurrentStatus => _currentStatus;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHours");
        var databasePath = Path.Combine(dataDirectory, "gamehours.db");

        _database = new GameHoursDatabase(databasePath);
        await _database.InitializeAsync(cancellationToken);

        var achievementCoordinator = new DesktopAchievementCoordinator(databasePath);
        _achievementMonitor = new ActiveAchievementMonitor(
            achievementCoordinator,
            _lifetime.Token);
        _achievementMonitor.AchievementUnlocked += HandleAchievementUnlocked;

        var discovery = new InstalledGameDiscoveryService(
            new IInstalledGameSource[]
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
        var trackingState = new SqliteTrackingStateRepository(_database);
        var openSessions = new SqliteOpenSessionRepository(_database);
        _historicalEvidence = new SqliteHistoricalEvidenceRepository(
            _database,
            trackingState,
            _sessions);

        var baseResolver = new WindowsGameResolver(installedGames);
        var resolver = new LearningGameResolver(baseResolver, _mappings, _games);
        var snapshotProvider = new WindowsProcessSnapshotProvider();
        var monitor = new HybridWindowsProcessMonitor(snapshotProvider, TimeSpan.FromSeconds(1));

        _engine = new GameSessionEngine(
            monitor,
            resolver,
            _games,
            _sessions,
            openSessions,
            trackingState);
        _engine.Notice += HandleTrackingNotice;

        await ReloadLocalDataAsync(cancellationToken);
        PublishStatus(isTracking: false, "Preparado para monitorizar");
    }

    public Task StartAsync()
    {
        ThrowIfDisposed();
        if (_engine is null)
        {
            throw new InvalidOperationException("DesktopHost must be initialized before tracking starts.");
        }

        if (_trackingTask is not null)
        {
            return Task.CompletedTask;
        }

        _trackingTask = RunTrackerAsync(_engine, _lifetime.Token);
        PublishStatus(isTracking: true, "Monitorizando juegos");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_trackingTask is null)
        {
            return;
        }

        PublishStatus(isTracking: true, "Guardando sesión activa…");
        GracefulShutdownSignal.Request();

        try
        {
            await _trackingTask.WaitAsync(cancellationToken);
        }
        finally
        {
            _trackingTask = null;
            PublishStatus(isTracking: false, "Detenido");
        }
    }

    public async Task RefreshLibraryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await ReloadLocalDataAsync(cancellationToken);
        PublishStatus(
            _trackingTask is not null,
            _trackingTask is null ? "Detenido" : "Monitorizando juegos");
    }

    private async Task RunTrackerAsync(GameSessionEngine engine, CancellationToken cancellationToken)
    {
        try
        {
            await engine.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishStatus(isTracking: false, $"Error del tracker: {exception.Message}");
        }
    }

    private void HandleTrackingNotice(TrackingNotice notice)
    {
        switch (notice.Type)
        {
            case TrackingNoticeType.SessionStarted:
                lock (_activeGate)
                {
                    _activeGames[notice.Game.Id] = new ActiveDesktopGame(
                        notice.Game.Title,
                        notice.AtUtc.ToUniversalTime());
                }
                PublishStatus(isTracking: true, $"Jugando a {notice.Game.Title}");
                _ = StartAchievementMonitoringAsync(notice);
                break;

            case TrackingNoticeType.SessionCompleted:
                lock (_activeGate)
                {
                    _activeGames.Remove(notice.Game.Id);
                }
                _ = StopAchievementMonitoringAsync(notice.Game.Id);
                PublishStatus(isTracking: true, "Monitorizando juegos");
                _ = RefreshLibraryAfterNoticeAsync();
                break;

            case TrackingNoticeType.SessionRecovered:
                _ = RefreshLibraryAfterNoticeAsync();
                break;
        }
    }

    private async Task StartAchievementMonitoringAsync(TrackingNotice notice)
    {
        if (_achievementMonitor is null || _mappings is null)
        {
            return;
        }

        try
        {
            var mappings = await _mappings.GetForGameAsync(
                notice.Game.Id,
                includeHelpers: false,
                _lifetime.Token);
            var executablePath = mappings
                .Select(mapping => mapping.ExecutablePath)
                .FirstOrDefault(File.Exists)
                ?? mappings.Select(mapping => mapping.ExecutablePath).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            lock (_activeGate)
            {
                if (!_activeGames.TryGetValue(notice.Game.Id, out var active) ||
                    active.StartedAtUtc != notice.AtUtc.ToUniversalTime())
                {
                    return;
                }
            }

            _achievementMonitor.Start(
                notice.Game.Id,
                notice.Game.Title,
                executablePath,
                notice.AtUtc);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // Achievement monitoring is optional and must never stop playtime tracking.
        }
    }

    private async Task StopAchievementMonitoringAsync(Guid gameId)
    {
        if (_achievementMonitor is null)
        {
            return;
        }

        try
        {
            await _achievementMonitor.StopAsync(gameId);
        }
        catch
        {
            // Final achievement reconciliation must not affect session completion.
        }
    }

    private void HandleAchievementUnlocked(DesktopAchievementUnlocked notice)
    {
        AchievementUnlocked?.Invoke(notice);
        _ = RefreshLibraryAfterNoticeAsync();
    }

    private async Task RefreshLibraryAfterNoticeAsync()
    {
        try
        {
            await RefreshLibraryAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            // A refresh failure must never stop playtime tracking.
        }
    }

    private async Task ReloadLocalDataAsync(CancellationToken cancellationToken)
    {
        if (_games is null ||
            _mappings is null ||
            _sessions is null ||
            _historicalEvidence is null ||
            _achievementActivity is null)
        {
            _library = Array.Empty<DesktopGameRow>();
            _recentActivity = Array.Empty<DesktopTimelineRow>();
            return;
        }

        var games = await _games.GetAllAsync(cancellationToken);
        var rows = new List<DesktopGameRow>(games.Count);
        var sessionsForTimeline = new List<DesktopActivityRow>();

        foreach (var game in games)
        {
            var sessions = await _sessions.GetForGameAsync(
                game.Id,
                cancellationToken: cancellationToken);
            var evidence = await _historicalEvidence.GetForGameAsync(game.Id, cancellationToken);
            var mappings = await _mappings.GetForGameAsync(
                game.Id,
                includeHelpers: false,
                cancellationToken);

            var measuredTicks = sessions.Aggregate(
                0L,
                (total, session) => checked(total + session.Duration.Ticks));
            var estimatedTicks = evidence.Aggregate(
                0L,
                (total, item) => checked(total + item.Duration.Ticks));

            DateTimeOffset? firstMeasuredSessionAtUtc = sessions.Count > 0
                ? sessions.Min(session => session.StartedAtUtc)
                : null;
            DateTimeOffset? lastMeasuredSessionAtUtc = sessions.Count > 0
                ? sessions.Max(session => session.EndedAtUtc)
                : null;

            DateTimeOffset? firstActivityAtUtc = firstMeasuredSessionAtUtc;
            DateTimeOffset? lastActivityAtUtc = lastMeasuredSessionAtUtc;

            if (evidence.Count > 0)
            {
                var firstEvidenceAtUtc = evidence.Min(item => item.PeriodStartUtc);
                var lastEvidenceAtUtc = evidence.Max(item => item.PeriodEndUtc);
                if (firstActivityAtUtc is null || firstEvidenceAtUtc < firstActivityAtUtc.Value)
                {
                    firstActivityAtUtc = firstEvidenceAtUtc;
                }

                if (lastActivityAtUtc is null || lastEvidenceAtUtc > lastActivityAtUtc.Value)
                {
                    lastActivityAtUtc = lastEvidenceAtUtc;
                }
            }

            var executablePath = mappings
                .Select(mapping => mapping.ExecutablePath)
                .FirstOrDefault(File.Exists)
                ?? mappings.Select(mapping => mapping.ExecutablePath).FirstOrDefault();

            var gameActivity = sessions
                .Select(session => new DesktopActivityRow(
                    session.Id,
                    game.Id,
                    game.Title,
                    session.StartedAtUtc,
                    session.EndedAtUtc,
                    session.Duration,
                    session.EndReason))
                .OrderByDescending(item => item.EndedAtUtc)
                .ToArray();

            rows.Add(new DesktopGameRow(
                game.Id,
                game.Title,
                TimeSpan.FromTicks(checked(measuredTicks + estimatedTicks)),
                TimeSpan.FromTicks(measuredTicks),
                TimeSpan.FromTicks(estimatedTicks),
                firstActivityAtUtc,
                lastActivityAtUtc,
                firstMeasuredSessionAtUtc,
                lastMeasuredSessionAtUtc,
                sessions.Count,
                executablePath,
                gameActivity.Take(20).ToArray()));

            sessionsForTimeline.AddRange(gameActivity);
        }

        _library = rows
            .OrderByDescending(row => row.TotalPlaytime)
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var achievementUnlocks = await _achievementActivity.GetRecentUnlocksAsync(
            limit: 50,
            cancellationToken: cancellationToken);

        _recentActivity = sessionsForTimeline
            .Select(session => new DesktopTimelineRow(
                session.GameId,
                session.GameTitle,
                session.EndedAtUtc,
                DesktopTimelineKind.Session,
                Duration: session.Duration,
                EndReason: session.EndReason))
            .Concat(achievementUnlocks.Select(unlock => new DesktopTimelineRow(
                unlock.GameId,
                unlock.GameTitle,
                unlock.OccurredAtUtc,
                DesktopTimelineKind.AchievementUnlocked,
                AchievementApiName: unlock.ApiName,
                AchievementDisplayName: AchievementPresentation.TimelineText(
                    unlock.DisplayName,
                    unlock.ApiName,
                    unlock.Description),
                IsObservedTimeFallback: unlock.IsObservedTimeFallback)))
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.Kind)
            .Take(50)
            .ToArray();
    }

    private void PublishStatus(bool isTracking, string statusText)
    {
        ActiveDesktopGame? activeGame;
        lock (_activeGate)
        {
            activeGame = _activeGames.Values
                .OrderBy(game => game.StartedAtUtc)
                .FirstOrDefault();
        }

        _currentStatus = new DesktopStatus(
            isTracking,
            statusText,
            activeGame?.Title,
            activeGame?.StartedAtUtc,
            _library,
            _recentActivity);
        StatusChanged?.Invoke(_currentStatus);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync();
        }
        finally
        {
            if (_engine is not null)
            {
                _engine.Notice -= HandleTrackingNotice;
            }

            if (_achievementMonitor is not null)
            {
                _achievementMonitor.AchievementUnlocked -= HandleAchievementUnlocked;
                await _achievementMonitor.DisposeAsync();
                _achievementMonitor = null;
            }

            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
