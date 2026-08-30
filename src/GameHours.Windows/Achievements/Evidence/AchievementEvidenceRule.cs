using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements.Evidence;

/// <summary>
/// One game-specific, positive-only rule evaluated against an already parsed state. Returning
/// false means "not proven", not "locked". Implementations should be deterministic and free of
/// I/O so parser concerns remain in the provider and rule behavior stays easy to unit test.
/// </summary>
public interface IAchievementEvidenceRule<in TState>
{
    string AchievementApiName { get; }
    string RuleId { get; }
    int Version { get; }

    bool TryProve(TState state, out string detail);
}

public static class AchievementEvidenceRuleEvaluator
{
    public static IReadOnlyList<ConfirmedAchievementUnlockEvidence> Evaluate<TState>(
        Guid gameId,
        AchievementEvidenceOrigin origin,
        string provider,
        TState state,
        IEnumerable<IAchievementEvidenceRule<TState>> rules,
        string? sourcePath,
        string? sourceFingerprint,
        DateTimeOffset observedAtUtc)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rules);

        var results = new List<ConfirmedAchievementUnlockEvidence>();
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            if (!rule.TryProve(state, out var detail))
            {
                continue;
            }

            results.Add(new ConfirmedAchievementUnlockEvidence(
                gameId,
                rule.AchievementApiName,
                origin,
                provider,
                rule.RuleId,
                rule.Version,
                sourcePath,
                sourceFingerprint,
                observedAtUtc,
                detail));
        }

        return results;
    }
}
