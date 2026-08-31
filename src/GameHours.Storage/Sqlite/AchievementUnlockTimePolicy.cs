using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

/// <summary>
/// Presentation/read-side policy for unlock timestamps that were already present when
/// GameHours first observed a GSE/Goldberg game. Those emulator timestamps can describe
/// when state was reconstructed or first persisted rather than the original historical unlock.
/// </summary>
public static class AchievementUnlockTimePolicy
{
    internal const string GseSourceMarker = "GSE/Goldberg";

    public static bool IsHistoricalTimeUnverified(StoredAchievement achievement)
    {
        ArgumentNullException.ThrowIfNull(achievement);
        return IsHistoricalTimeUnverified(
            achievement.Source,
            achievement.FirstSeenAtUtc,
            achievement.FirstUnlockedSeenAtUtc);
    }

    internal static bool IsHistoricalTimeUnverified(
        string source,
        DateTimeOffset firstSeenAtUtc,
        DateTimeOffset? firstUnlockedSeenAtUtc) =>
        firstUnlockedSeenAtUtc is DateTimeOffset firstUnlockedSeen &&
        firstUnlockedSeen == firstSeenAtUtc &&
        source.Contains(GseSourceMarker, StringComparison.OrdinalIgnoreCase);
}
