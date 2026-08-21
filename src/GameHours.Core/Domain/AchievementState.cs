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
