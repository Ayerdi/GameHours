using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Decides which persisted locked-to-unlocked transitions are safe to surface during
/// one measured game session. An immediate first read establishes a silent session baseline;
/// a late first read may emit transitions when GameHours already had a durable baseline from
/// an earlier observation. This supports formats that flush achievement state only on exit.
/// </summary>
public sealed class AchievementSessionNotificationGate
{
    private readonly DateTimeOffset _sessionStartedAtUtc;
    private readonly TimeSpan _clockTolerance;
    private readonly TimeSpan _initialBaselineWindow;
    private readonly HashSet<string> _emitted = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasReadableBaseline;

    public AchievementSessionNotificationGate(
        DateTimeOffset sessionStartedAtUtc,
        TimeSpan? clockTolerance = null,
        TimeSpan? initialBaselineWindow = null)
    {
        _sessionStartedAtUtc = sessionStartedAtUtc.ToUniversalTime();
        _clockTolerance = clockTolerance ?? TimeSpan.FromSeconds(5);
        _initialBaselineWindow = initialBaselineWindow ?? TimeSpan.FromSeconds(3);
        if (_clockTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(clockTolerance));
        }

        if (_initialBaselineWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBaselineWindow));
        }
    }

    public bool HasReadableBaseline => _hasReadableBaseline;

    public IReadOnlyList<StoredAchievement> AcceptReadableObservation(
        DateTimeOffset observedAtUtc,
        bool isInitialPersistentBaseline,
        IReadOnlyList<StoredAchievement> notificationCandidates)
    {
        ArgumentNullException.ThrowIfNull(notificationCandidates);
        var observedAt = observedAtUtc.ToUniversalTime();

        if (!_hasReadableBaseline)
        {
            _hasReadableBaseline = true;

            // Never notify historical unlocks while GameHours is establishing the durable
            // baseline for this game. If a durable baseline already existed, only an immediate
            // first read is treated as the session baseline. A much later first read can be an
            // exit-flush transition from the session that just ran.
            if (isInitialPersistentBaseline ||
                observedAt <= _sessionStartedAtUtc + _initialBaselineWindow)
            {
                return Array.Empty<StoredAchievement>();
            }
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
