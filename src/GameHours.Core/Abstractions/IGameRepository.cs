using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface IGameRepository
{
    Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default);
}
