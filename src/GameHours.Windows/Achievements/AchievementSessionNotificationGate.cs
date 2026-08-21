using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Decides which persisted locked-to-unlocked transitions are safe to surface during
/// one measured game session. The first readable snapshot establishes a silent baseline;
/// later transitions are emitted once and obviously stale unlock timestamps are rejected.
/// </summary>
public sealed class AchievementSessionNotificationGate
{
    private readonly DateTimeOffset _sessionStartedAtUtc;
    private readonly TimeSpan _clockTolerance;
    private readonly HashSet<string> _emitted = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasReadableBaseline;

    public AchievementSessionNotificationGate(
        DateTimeOffset sessionStartedAtUtc,
        TimeSpan? clockTolerance = null)
    {
        _sessionStartedAtUtc = sessionStartedAtUtc.ToUniversalTime();
        _clockTolerance = clockTolerance ?? TimeSpan.FromSeconds(5);
        if (_clockTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(clockTolerance));
        }
    }

    public bool HasReadableBaseline => _hasReadableBaseline;

    public IReadOnlyList<StoredAchievement> AcceptReadableObservation(
        IReadOnlyList<StoredAchievement> notificationCandidates)
    {
        ArgumentNullException.ThrowIfNull(notificationCandidates);

        if (!_hasReadableBaseline)
        {
            _hasReadableBaseline = true;
            return Array.Empty<StoredAchievement>();
        }

        var accepted = new List<StoredAchievement>();
        foreach (var achievement in notificationCandidates)
        {
            if (!achievement.IsUnlocked ||
                _emitted.Contains(achievement.ApiName) ||
                IsClearlyOlderThanSession(achievement.UnlockedAtUtc))
            {
                continue;
            }

            _emitted.Add(achievement.ApiName);
            accepted.Add(achievement);
        }

        return accepted;
    }

    private bool IsClearlyOlderThanSession(DateTimeOffset? unlockedAtUtc)
    {
        if (unlockedAtUtc is null)
        {
            return false;
        }

        return unlockedAtUtc.Value.ToUniversalTime() < _sessionStartedAtUtc - _clockTolerance;
    }
}
