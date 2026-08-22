namespace GameHours.Core.Abstractions;

public sealed record SessionActivityMetrics(
    Guid SessionId,
    Guid GameId,
    TimeSpan FocusedDuration,
    TimeSpan ActiveDuration,
    TimeSpan IdleThreshold,
    bool IsFinalized,
    DateTimeOffset UpdatedAtUtc);

public interface ISessionActivityRepository
{
    Task UpsertAsync(
        SessionActivityMetrics metrics,
        CancellationToken cancellationToken = default);

    Task<SessionActivityMetrics?> GetBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionActivityMetrics>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
