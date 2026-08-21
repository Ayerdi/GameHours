using GameHours.Core.Abstractions;
using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements;

public sealed record LocalAchievementObservationResult(
    LocalAchievementSnapshot Snapshot,
    AchievementApplyResult Persistence,
    bool IsBaseline,
    IReadOnlyList<StoredAchievement> NotificationCandidates);

/// <summary>
/// Reconciles the current local achievement snapshot with GameHours persistence.
/// The first successful observation for a game is treated as a baseline so historical unlocks
/// are not presented as newly-earned notifications. The baseline is tracked even when the
/// current source reports zero unlocked achievements.
/// </summary>
public sealed class LocalAchievementObservationService
{
    private readonly ILocalAchievementProvider _provider;
    private readonly IAchievementRepository _repository;

    public LocalAchievementObservationService(
        ILocalAchievementProvider provider,
        IAchievementRepository repository)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<LocalAchievementObservationResult?> ObserveAsync(
        Guid gameId,
        string executablePath,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var snapshot = _provider.TryRead(executablePath);
        if (snapshot is null)
        {
            return null;
        }

        var isBaseline = !await _repository.HasObservedGameAsync(gameId, cancellationToken);
        var observations = snapshot.Achievements
            .Select(achievement => new AchievementObservation(
                achievement.ApiName,
                achievement.DisplayName,
                achievement.Description,
                achievement.Hidden,
                achievement.IsUnlocked,
                achievement.UnlockedAtUtc))
            .ToArray();

        var persistence = await _repository.ApplySnapshotAsync(
            gameId,
            observations,
            snapshot.Source,
            snapshot.IsCatalogueComplete,
            observedAtUtc,
            cancellationToken);

        var notificationCandidates = isBaseline
            ? Array.Empty<StoredAchievement>()
            : persistence.NewlyUnlocked;

        return new LocalAchievementObservationResult(
            snapshot,
            persistence,
            isBaseline,
            notificationCandidates);
    }
}
