using GameHours.Core.Abstractions;
using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements;

public sealed record LocalAchievementObservationResult(
    LocalAchievementSnapshot Snapshot,
    AchievementApplyResult Persistence,
    bool IsBaseline,
    IReadOnlyList<StoredAchievement> NotificationCandidates);

public sealed record LocalAchievementObservationAttempt(
    AchievementReadResult ReadResult,
    LocalAchievementObservationResult? Observation);

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
        CancellationToken cancellationToken = default) =>
        (await ObserveDetailedAsync(
            gameId,
            executablePath,
            observedAtUtc,
            cancellationToken)).Observation;

    /// <summary>
    /// Performs the same reconciliation as <see cref="ObserveAsync"/> while preserving the
    /// structured read result when no trustworthy snapshot can be persisted. This lets runtime
    /// diagnostics distinguish absence, unsupported formats, ambiguity and parser failure.
    /// </summary>
    public async Task<LocalAchievementObservationAttempt> ObserveDetailedAsync(
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

        var readResult = _provider.TryReadDetailed(executablePath);
        if (!readResult.IsSuccess || readResult.Snapshot is null)
        {
            return new LocalAchievementObservationAttempt(readResult, Observation: null);
        }

        var snapshot = readResult.Snapshot;
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

        var observation = new LocalAchievementObservationResult(
            snapshot,
            persistence,
            isBaseline,
            notificationCandidates);
        return new LocalAchievementObservationAttempt(readResult, observation);
    }
}
