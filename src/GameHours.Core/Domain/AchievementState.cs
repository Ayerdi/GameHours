namespace GameHours.Core.Domain;

public sealed record AchievementObservation(
    string ApiName,
    string DisplayName,
    string Description,
    bool Hidden,
    bool IsUnlocked,
    DateTimeOffset? UnlockedAtUtc)
{
    public AchievementObservation Normalize()
    {
        if (string.IsNullOrWhiteSpace(ApiName))
        {
            throw new ArgumentException("Achievement API name cannot be empty.", nameof(ApiName));
        }

        var apiName = ApiName.Trim();
        return this with
        {
            ApiName = apiName,
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? apiName : DisplayName.Trim(),
            Description = Description?.Trim() ?? string.Empty,
            UnlockedAtUtc = IsUnlocked ? UnlockedAtUtc?.ToUniversalTime() : null
        };
    }
}

public sealed record StoredAchievement(
    Guid GameId,
    string ApiName,
    string DisplayName,
    string Description,
    bool Hidden,
    bool IsUnlocked,
    DateTimeOffset? UnlockedAtUtc,
    string Source,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? FirstUnlockedSeenAtUtc);

public sealed record AchievementApplyResult(
    IReadOnlyList<StoredAchievement> Current,
    IReadOnlyList<StoredAchievement> NewlyUnlocked);

/// <summary>
/// Describes how much of the user's locked/unlocked state the persisted observation could prove.
/// This is deliberately independent from catalogue completeness: a source can know every
/// achievement definition while only retaining positive evidence for unlocked achievements.
/// </summary>
public enum AchievementStateEvidenceCoverage
{
    Unknown = 0,
    UnlocksOnly = 1,
    Complete = 2
}

/// <summary>
/// Durable achievement summary for one remembered game. KnownCount is authoritative as a
/// catalogue total only when HasCompleteCatalogue is true; otherwise it is merely the number
/// of achievement IDs GameHours has observed locally so far. StateCoverage describes whether
/// the latest successful observation can prove the user's full locked/unlocked state.
/// </summary>
public sealed record AchievementGameSummary(
    Guid GameId,
    int KnownCount,
    int UnlockedCount,
    bool HasCompleteCatalogue,
    DateTimeOffset? FirstUnlockedAtUtc,
    DateTimeOffset? LastUnlockedAtUtc,
    DateTimeOffset? LastObservedAtUtc,
    string? LastSource,
    AchievementStateEvidenceCoverage StateCoverage = AchievementStateEvidenceCoverage.Unknown)
{
    public bool IsComplete =>
        HasCompleteCatalogue &&
        KnownCount > 0 &&
        UnlockedCount >= KnownCount;

    public double? CompletionPercentage =>
        HasCompleteCatalogue && KnownCount > 0
            ? Math.Clamp(UnlockedCount * 100d / KnownCount, 0d, 100d)
            : null;
}

/// <summary>
/// One durable achievement-unlock activity item. OccurredAtUtc prefers the source's real
/// unlock timestamp. When that timestamp is unavailable, GameHours falls back to the first
/// time it observed the achievement unlocked and marks IsObservedTimeFallback accordingly.
/// </summary>
public sealed record AchievementUnlockActivity(
    Guid GameId,
    string GameTitle,
    string ApiName,
    string DisplayName,
    string Description,
    bool Hidden,
    DateTimeOffset OccurredAtUtc,
    bool IsObservedTimeFallback,
    string Source);
