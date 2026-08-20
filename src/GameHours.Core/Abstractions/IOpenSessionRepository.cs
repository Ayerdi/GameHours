using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface IOpenSessionRepository
{
    Task UpsertAsync(
        OpenSessionCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenSessionCheckpoint>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
