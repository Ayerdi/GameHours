using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal sealed record DesktopSessionDetail(
    Guid SessionId,
    string GameTitle,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    TimeSpan Duration,
    CaptureMethod CaptureMethod,
    Confidence Confidence,
    string? EndReason,
    bool HasActivityTelemetry,
    bool AfkFilterEnabled,
    TimeSpan? FocusedDuration,
    TimeSpan? ActiveDuration,
    TimeSpan? AfkDuration,
    TimeSpan? IdleThreshold,
    TimeSpan? UnfocusedOrUnknownDuration);

internal sealed class DesktopSessionDetailService
{
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteSessionActivityRepository _activity;
    private readonly SqliteGameRepository _games;

    public DesktopSessionDetailService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var database = new GameHoursDatabase(databasePath);
        _sessions = new SqliteSessionRepository(database);
        _activity = new SqliteSessionActivityRepository(database);
        _games = new SqliteGameRepository(database);
    }

    public async Task<DesktopSessionDetail?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) return null;

        var sessionTask = _sessions.GetByIdAsync(sessionId, cancellationToken);
        var activityTask = _activity.GetBySessionIdAsync(sessionId, cancellationToken);
        await Task.WhenAll(sessionTask, activityTask);

        var session = await sessionTask;
        if (session is null) return null;

        var gameTitle = (await _games.GetAllAsync(cancellationToken))
            .FirstOrDefault(game => game.Id == session.GameId)?.Title ?? "Juego";
        var metrics = await activityTask;

        TimeSpan? focused = null;
        TimeSpan? active = null;
        TimeSpan? afk = null;
        TimeSpan? threshold = null;
        TimeSpan? unfocusedOrUnknown = null;
        var afkEnabled = false;

        if (metrics is not null)
        {
            focused = Clamp(metrics.FocusedDuration, TimeSpan.Zero, session.Duration);
            unfocusedOrUnknown = session.Duration - focused.Value;
            afkEnabled = metrics.AfkFilterEnabled;
            if (afkEnabled)
            {
                active = Clamp(metrics.ActiveDuration, TimeSpan.Zero, focused.Value);
                afk = focused.Value - active.Value;
                threshold = metrics.IdleThreshold;
            }
        }

        return new DesktopSessionDetail(
            session.Id,
            gameTitle,
            session.StartedAtUtc,
            session.EndedAtUtc,
            session.Duration,
            session.CaptureMethod,
            session.Confidence,
            session.EndReason,
            metrics is not null,
            afkEnabled,
            focused,
            active,
            afk,
            threshold,
            unfocusedOrUnknown);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
