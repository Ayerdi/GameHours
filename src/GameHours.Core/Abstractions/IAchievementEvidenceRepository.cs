using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

/// <summary>
/// Persists supplemental positive achievement proofs without collapsing their provenance into
/// platform/emulator achievement state. Saving the same proof again is idempotent and refreshes
/// its latest observation metadata.
/// </summary>
public interface IAchievementEvidenceRepository
{
    Task SaveAsync(
        Guid gameId,
        IReadOnlyList<ConfirmedAchievementUnlockEvidence> evidence,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredAchievementUnlockEvidence>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default);
}
