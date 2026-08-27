using GameHours.Core.Discovery;

namespace GameHours.Core.Abstractions;

public interface IGameCandidateRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task ObserveAsync(
        GameCandidateObservation observation,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameCandidate>> GetPendingAsync(
        CancellationToken cancellationToken = default);

    Task<int> GetPendingCountAsync(
        CancellationToken cancellationToken = default);

    Task ResolveAsync(
        string executablePath,
        ExecutableRole decisionRole,
        Guid? gameId = null,
        CancellationToken cancellationToken = default);
}
