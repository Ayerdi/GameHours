namespace GameHours.Core.Domain;

/// <summary>
/// Stable identity of one active achievement-evidence rule revision. Provider, achievement API
/// name and rule ID are compared case-insensitively; the revision number is exact.
/// </summary>
public readonly struct AchievementEvidenceRuleIdentity : IEquatable<AchievementEvidenceRuleIdentity>
{
    public string Provider { get; }
    public string AchievementApiName { get; }
    public string RuleId { get; }
    public int RuleVersion { get; }

    public AchievementEvidenceRuleIdentity(
        string provider,
        string achievementApiName,
        string ruleId,
        int ruleVersion)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Evidence provider cannot be empty.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(achievementApiName))
        {
            throw new ArgumentException("Achievement API name cannot be empty.", nameof(achievementApiName));
        }

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException("Evidence rule id cannot be empty.", nameof(ruleId));
        }

        if (ruleVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleVersion), "Evidence rule version must be positive.");
        }

        Provider = provider.Trim();
        AchievementApiName = achievementApiName.Trim();
        RuleId = ruleId.Trim();
        RuleVersion = ruleVersion;
    }

    public static AchievementEvidenceRuleIdentity From(ConfirmedAchievementUnlockEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new AchievementEvidenceRuleIdentity(
            evidence.Provider,
            evidence.ApiName,
            evidence.RuleId,
            evidence.RuleVersion);
    }

    public static AchievementEvidenceRuleIdentity From(StoredAchievementUnlockEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new AchievementEvidenceRuleIdentity(
            evidence.Provider,
            evidence.ApiName,
            evidence.RuleId,
            evidence.RuleVersion);
    }

    public bool Equals(AchievementEvidenceRuleIdentity other) =>
        RuleVersion == other.RuleVersion &&
        string.Equals(Provider, other.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(AchievementApiName, other.AchievementApiName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(RuleId, other.RuleId, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) =>
        obj is AchievementEvidenceRuleIdentity other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Provider, StringComparer.OrdinalIgnoreCase);
        hash.Add(AchievementApiName, StringComparer.OrdinalIgnoreCase);
        hash.Add(RuleId, StringComparer.OrdinalIgnoreCase);
        hash.Add(RuleVersion);
        return hash.ToHashCode();
    }

    public static bool operator ==(AchievementEvidenceRuleIdentity left, AchievementEvidenceRuleIdentity right) =>
        left.Equals(right);

    public static bool operator !=(AchievementEvidenceRuleIdentity left, AchievementEvidenceRuleIdentity right) =>
        !left.Equals(right);
}

/// <summary>
/// Keeps durable evidence auditable while ensuring only rule revisions declared active by the
/// current application can affect achievement projections. Superseded or removed rules therefore
/// fail closed without deleting their historical rows or requiring a revocation column.
/// </summary>
public static class AchievementEvidenceRulePolicy
{
    public static IReadOnlyList<ConfirmedAchievementUnlockEvidence> KeepActive(
        IEnumerable<ConfirmedAchievementUnlockEvidence> evidence,
        IEnumerable<AchievementEvidenceRuleIdentity> activeRules)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var active = BuildActiveSet(activeRules);
        var results = new List<ConfirmedAchievementUnlockEvidence>();
        foreach (var item in evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (active.Contains(AchievementEvidenceRuleIdentity.From(item)))
            {
                results.Add(item);
            }
        }

        return results;
    }

    public static IReadOnlyList<StoredAchievementUnlockEvidence> KeepActive(
        IEnumerable<StoredAchievementUnlockEvidence> evidence,
        IEnumerable<AchievementEvidenceRuleIdentity> activeRules)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var active = BuildActiveSet(activeRules);
        var results = new List<StoredAchievementUnlockEvidence>();
        foreach (var item in evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (active.Contains(AchievementEvidenceRuleIdentity.From(item)))
            {
                results.Add(item);
            }
        }

        return results;
    }

    private static HashSet<AchievementEvidenceRuleIdentity> BuildActiveSet(
        IEnumerable<AchievementEvidenceRuleIdentity> activeRules)
    {
        ArgumentNullException.ThrowIfNull(activeRules);
        return activeRules.ToHashSet();
    }
}
