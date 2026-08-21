using GameHours.Core.Domain;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class AchievementSessionNotificationGateTests
{
    [Fact]
    public void ImmediateFirstReadableObservation_IsSilentSessionBaseline()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt);

        var result = gate.AcceptReadableObservation(
            startedAt.AddSeconds(1),
            isInitialPersistentBaseline: false,
            new[] { Stored("ACH_OLD", startedAt) });

        Assert.True(gate.HasReadableBaseline);
        Assert.Empty(result);
    }

    [Fact]
    public void FirstEverPersistentObservation_IsSilentEvenWhenReadLate()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt);

        var result = gate.AcceptReadableObservation(
            startedAt.AddMinutes(30),
            isInitialPersistentBaseline: true,
            new[] { Stored("ACH_HISTORICAL", startedAt.AddMinutes(20)) });

        Assert.True(gate.HasReadableBaseline);
        Assert.Empty(result);
    }

    [Fact]
    public void LateFirstRead_WithExistingPersistentBaseline_CanSurfaceExitFlushUnlock()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(
            startedAt,
            initialBaselineWindow: TimeSpan.FromSeconds(3));
        var achievement = Stored("ACH_EXIT_FLUSH", startedAt.AddMinutes(10));

        var result = gate.AcceptReadableObservation(
            startedAt.AddMinutes(10).AddSeconds(1),
            isInitialPersistentBaseline: false,
            new[] { achievement });

        Assert.Equal("ACH_EXIT_FLUSH", Assert.Single(result).ApiName);
    }

    [Fact]
    public void LaterUnlockDuringSession_IsAcceptedOnlyOnce()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt);
        gate.AcceptReadableObservation(
            startedAt.AddSeconds(1),
            isInitialPersistentBaseline: false,
            Array.Empty<StoredAchievement>());
        var achievement = Stored("ACH_NEW", startedAt.AddMinutes(10));

        var first = gate.AcceptReadableObservation(
            startedAt.AddMinutes(10),
            isInitialPersistentBaseline: false,
            new[] { achievement });
        var repeated = gate.AcceptReadableObservation(
            startedAt.AddMinutes(11),
            isInitialPersistentBaseline: false,
            new[] { achievement });

        Assert.Equal("ACH_NEW", Assert.Single(first).ApiName);
        Assert.Empty(repeated);
    }

    [Fact]
    public void LaterCandidateWithClearlyOldTimestamp_IsRejected()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt, TimeSpan.FromSeconds(5));
        gate.AcceptReadableObservation(
            startedAt.AddSeconds(1),
            isInitialPersistentBaseline: false,
            Array.Empty<StoredAchievement>());

        var result = gate.AcceptReadableObservation(
            startedAt.AddMinutes(1),
            isInitialPersistentBaseline: false,
            new[] { Stored("ACH_STALE", startedAt.AddMinutes(-20)) });

        Assert.Empty(result);
    }

    [Fact]
    public void MissingUnlockTimestamp_CanStillBeAcceptedAfterBaseline()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-21T14:00:00Z");
        var gate = new AchievementSessionNotificationGate(startedAt);
        gate.AcceptReadableObservation(
            startedAt.AddSeconds(1),
            isInitialPersistentBaseline: false,
            Array.Empty<StoredAchievement>());

        var result = gate.AcceptReadableObservation(
            startedAt.AddMinutes(1),
            isInitialPersistentBaseline: false,
            new[] { Stored("ACH_NO_TIME", null) });

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
