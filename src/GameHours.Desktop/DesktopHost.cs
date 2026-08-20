using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Tracking;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Processes;

namespace GameHours.Desktop;

public sealed record DesktopGameRow(
    Guid GameId,
    string Title,
    TimeSpan TotalPlaytime,
    TimeSpan MeasuredPlaytime,
    TimeSpan EstimatedPlaytime);

public sealed record DesktopStatus(
    bool IsTracking,
    string StatusText,
    string? ActiveGameTitle,
    IReadOnlyList<DesktopGameRow> Games);

public sealed class DesktopHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _activeGate = new();
    private readonly Dictionary<Guid, string> _activeGames = new();

    private GameHoursDatabase? _database;
    private SqliteGameRepository? _games;
    private SqliteSessionRepository? _sessions;
    private SqliteHistoricalEvidenceRepository? _historicalEvidence;
    private GameSessionEngine? _engine;
    private Task? _trackingTask;
    private IReadOnlyList<DesktopGameRow> _library = Array.Empty<DesktopGameRow>();
    private DesktopStatus _currentStatus = new(false, "Preparando…", null, Array.Empty<DesktopGameRow>());
    private bool _disposed;

    public event Action<DesktopStatus>? StatusChanged;

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

        var discovery = new InstalledGameDiscoveryService(
            new IInstalledGameSource[]
            {
                new SteamInstalledGameSource(),
                new EpicInstalledGameSource(),
                new GogInstalledGameSource()
            });
        var installedGames = await discovery.DiscoverAsync(cancellationToken);

        _games = new SqliteGameRepository(_database);
        var mappings = new SqliteExecutableMappingRepository(_database);
        _sessions = new SqliteSessionRepository(_database);
        var trackingState = new SqliteTrackingStateRepository(_database);
        var openSessions = new SqliteOpenSessionRepository(_database);
        _historicalEvidence = new SqliteHistoricalEvidenceRepository(
            _database,
            trackingState,
            _sessions);

        var baseResolver = new WindowsGameResolver(installedGames);
        var resolver = new LearningGameResolver(baseResolver, mappings, _games);
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

        _library = await LoadLibraryAsync(cancellationToken);
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
        _library = await LoadLibraryAsync(cancellationToken);
        PublishStatus(_trackingTask is not null, _trackingTask is null ? "Detenido" : "Monitorizando juegos");
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
                    _activeGames[notice.Game.Id] = notice.Game.Title;
                }
                PublishStatus(isTracking: true, $"Jugando a {notice.Game.Title}");
                break;

            case TrackingNoticeType.SessionCompleted:
                lock (_activeGate)
                {
                    _activeGames.Remove(notice.Game.Id);
                }
                PublishStatus(isTracking: true, "Monitorizando juegos");
                _ = RefreshLibraryAfterNoticeAsync();
                break;

            case TrackingNoticeType.SessionRecovered:
                _ = RefreshLibraryAfterNoticeAsync();
                break;
        }
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

    private async Task<IReadOnlyList<DesktopGameRow>> LoadLibraryAsync(
        CancellationToken cancellationToken)
    {
        if (_games is null || _sessions is null || _historicalEvidence is null)
        {
            return Array.Empty<DesktopGameRow>();
        }

        var games = await _games.GetAllAsync(cancellationToken);
        var rows = new List<DesktopGameRow>(games.Count);
        foreach (var game in games)
        {
            var sessions = await _sessions.GetForGameAsync(
                game.Id,
                cancellationToken: cancellationToken);
            var evidence = await _historicalEvidence.GetForGameAsync(game.Id, cancellationToken);

            var measuredTicks = sessions.Aggregate(
                0L,
                (total, session) => checked(total + session.Duration.Ticks));
            var estimatedTicks = evidence.Aggregate(
                0L,
                (total, item) => checked(total + item.Duration.Ticks));

            rows.Add(new DesktopGameRow(
                game.Id,
                game.Title,
                TimeSpan.FromTicks(checked(measuredTicks + estimatedTicks)),
                TimeSpan.FromTicks(measuredTicks),
                TimeSpan.FromTicks(estimatedTicks)));
        }

        return rows
            .OrderByDescending(row => row.TotalPlaytime)
            .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void PublishStatus(bool isTracking, string statusText)
    {
        string? activeGame;
        lock (_activeGate)
        {
            activeGame = _activeGames.Values.FirstOrDefault();
        }

        var status = new DesktopStatus(
            isTracking,
            statusText,
            activeGame,
            _library);
        _currentStatus = status;
        StatusChanged?.Invoke(status);
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
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DesktopHost));
        }
    }
}
