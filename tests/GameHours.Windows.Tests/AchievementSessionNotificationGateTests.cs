using GameHours.Core.Domain;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class AchievementSessionNotificationGateTests
{
    [Fact]
    public void FirstReadableObservation_IsAlwaysSilentBaseline()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt);

        var result = gate.AcceptReadableObservation(new[]
        {
            Stored("ACH_OLD", startedAt.AddHours(-2))
        });

        Assert.True(gate.HasReadableBaseline);
        Assert.Empty(result);
    }

    [Fact]
    public void LaterUnlockDuringSession_IsAcceptedOnlyOnce()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt);
        gate.AcceptReadableObservation(Array.Empty<StoredAchievement>());
        var achievement = Stored("ACH_NEW", startedAt.AddMinutes(10));

        var first = gate.AcceptReadableObservation(new[] { achievement });
        var repeated = gate.AcceptReadableObservation(new[] { achievement });

        Assert.Equal("ACH_NEW", Assert.Single(first).ApiName);
        Assert.Empty(repeated);
    }

    [Fact]
    public void LaterCandidateWithClearlyOldTimestamp_IsRejected()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt, TimeSpan.FromSeconds(5));
        gate.AcceptReadableObservation(Array.Empty<StoredAchievement>());

        var result = gate.AcceptReadableObservation(new[]
        {
            Stored("ACH_STALE", startedAt.AddMinutes(-20))
        });

        Assert.Empty(result);
    }

    [Fact]
    public void MissingUnlockTimestamp_CanStillBeAcceptedAfterBaseline()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt);
        gate.AcceptReadableObservation(Array.Empty<StoredAchievement>());

        var result = gate.AcceptReadableObservation(new[]
        {
            Stored("ACH_NO_TIME", null)
        });

        Assert.Equal("ACH_NO_TIME", Assert.Single(result).ApiName);
    }

    private static StoredAchievement Stored(string apiName, DateTimeOffset? unlockedAtUtc) =>
        new(
            Guid.NewGuid(),
            apiName,
            apiName,
            string.Empty,
            Hidden: false,
            IsUnlocked: true,
            UnlockedAtUtc: unlockedAtUtc,
            Source: "test",
            FirstSeenAtUtc: DateTimeOffset.Parse("2026-08-21T13:00:00Z"),
            LastSeenAtUtc: DateTimeOffset.Parse("2026-08-21T14:10:00Z"),
            FirstUnlockedSeenAtUtc: DateTimeOffset.Parse("2026-08-21T14:10:00Z"));
}
