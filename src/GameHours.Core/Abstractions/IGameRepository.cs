using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface IGameRepository
{
    Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default);

    Task<TrackedGame?> GetByIdAsync(
        Guid gameId,
        CancellationToken cancellationToken = default);

    Task<TrackedGame?> GetByTitleAsync(
        string title,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default);
}
