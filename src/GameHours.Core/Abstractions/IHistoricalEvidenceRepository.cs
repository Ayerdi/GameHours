using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface IHistoricalEvidenceRepository
{
    Task<bool> AddAsync(HistoricalEvidence evidence, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoricalEvidence>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default);
}
