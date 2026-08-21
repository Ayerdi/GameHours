using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface IAchievementRepository
{
    Task<AchievementApplyResult> ApplySnapshotAsync(
        Guid gameId,
        IReadOnlyList<AchievementObservation> observations,
        string source,
        bool hasCompleteCatalogue,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredAchievement>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default);
}
