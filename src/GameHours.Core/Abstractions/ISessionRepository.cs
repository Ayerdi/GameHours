using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface ISessionRepository
{
    Task<bool> AddAsync(PlaySession session, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaySession>> GetForGameAsync(
        Guid gameId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default);

    Task<bool> HasOverlapAsync(
        Guid gameId,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken cancellationToken = default);
}
