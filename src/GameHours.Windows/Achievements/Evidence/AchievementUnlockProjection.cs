using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements.Evidence;

/// <summary>
/// User-state projection after reconciling platform/emulator state with supplemental evidence.
/// IsExact is true only when the primary source proves the complete locked/unlocked state.
/// Otherwise ConfirmedApiNames is a lower bound and must not be interpreted as the full set.
/// </summary>
public sealed record AchievementUnlockProjection(
    IReadOnlyList<string> ConfirmedApiNames,
    bool IsExact,
    int SupplementalConfirmedCount)
{
    public int ConfirmedCount => ConfirmedApiNames.Count;
}

public static class AchievementEvidenceReconciler
{
    public static AchievementUnlockProjection Reconcile(
        Guid gameId,
        LocalAchievementSnapshot? primarySnapshot,
        AchievementStateCoverage primaryCoverage,
        IEnumerable<ConfirmedAchievementUnlockEvidence> supplementalEvidence,
        IEnumerable<AchievementEvidenceRuleIdentity> activeRules)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        ArgumentNullException.ThrowIfNull(supplementalEvidence);
        ArgumentNullException.ThrowIfNull(activeRules);

        var primaryUnlocked = primarySnapshot?.Achievements
            .Where(achievement => achievement.IsUnlocked)
            .Select(achievement => achievement.ApiName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        // A source explicitly classified as Complete owns the locked/unlocked truth for this
        // game/profile. Supplemental save evidence remains auditable but must not override an
        // authoritative locked state, which could otherwise mix saves from another profile.
        if (primarySnapshot is not null && primaryCoverage == AchievementStateCoverage.Complete)
        {
            return new AchievementUnlockProjection(
                Order(primaryUnlocked),
                IsExact: true,
                SupplementalConfirmedCount: 0);
        }

        // Durable evidence is intentionally not deleted when a rule is corrected or removed.
        // Only revisions explicitly declared active by the current application may contribute
        // to the effective projection; old rows remain available solely for auditability.
        var supplemental = AchievementEvidenceRulePolicy.KeepActive(
                supplementalEvidence.Where(evidence => evidence.GameId == gameId),
                activeRules)
            .Select(evidence => evidence.ApiName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var primarySet = primaryUnlocked.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supplementalNewCount = supplemental.Count(apiName => !primarySet.Contains(apiName));
        var combined = primaryUnlocked
            .Concat(supplemental)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new AchievementUnlockProjection(
            Order(combined),
            IsExact: false,
            SupplementalConfirmedCount: supplementalNewCount);
    }

    private static IReadOnlyList<string> Order(IEnumerable<string> apiNames) =>
        apiNames
            .OrderBy(apiName => apiName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
