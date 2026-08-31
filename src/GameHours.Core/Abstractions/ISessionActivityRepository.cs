namespace GameHours.Core.Abstractions;

public sealed record SessionActivityMetrics(
    Guid SessionId,
    Guid GameId,
    TimeSpan FocusedDuration,
    TimeSpan ActiveDuration,
    TimeSpan IdleThreshold,
    bool AfkFilterEnabled,
    bool IsFinalized,
    DateTimeOffset UpdatedAtUtc)
{
    // Source-compatible constructor for existing callers and imported/test data. Zero is a
    // first-class persisted value meaning that AFK filtering was disabled for this session.
    public SessionActivityMetrics(
        Guid sessionId,
        Guid gameId,
        TimeSpan focusedDuration,
        TimeSpan activeDuration,
        TimeSpan idleThreshold,
        bool isFinalized,
        DateTimeOffset updatedAtUtc)
        : this(
            sessionId,
            gameId,
            focusedDuration,
            activeDuration,
            idleThreshold,
            idleThreshold > TimeSpan.Zero,
            isFinalized,
            updatedAtUtc)
    {
    }
}

public interface ISessionActivityRepository
{
    Task UpsertAsync(
        SessionActivityMetrics metrics,
        CancellationToken cancellationToken = default);

    Task<SessionActivityMetrics?> GetBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionActivityMetrics>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionActivityMetrics>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
