using GameHours.Desktop;
using GameHours.Core.Domain;

namespace GameHours.Windows.Tests;

public sealed class AchievementNotificationPresentationTests
{
    [Fact]
    public void Build_PreservesUsefulAchievementContextAndNormalizesWhitespace()
    {
        var notice = new DesktopAchievementUnlocked(
            Guid.NewGuid(),
            "Gothic 1 Remake\r\n",
            Stored(
                "ACH_FIRST",
                "Primer\tlogro",
                "Descripción\r\ncon salto"));

        var presentation = AchievementNotificationPresentation.Build(notice);

        Assert.Equal("Logro desbloqueado", presentation.Title);
        Assert.Equal(new[] { "Primer logro", "Descripción con salto", "Gothic 1 Remake" }, presentation.BodyLines);
    }

    [Fact]
    public void Build_FallsBackToApiNameWhenDisplayNameIsMissing()
    {
        var notice = new DesktopAchievementUnlocked(
            Guid.NewGuid(),
            "Game",
            Stored(
                "ACH_ONLY",
                " ",
                string.Empty));

        var presentation = AchievementNotificationPresentation.Build(notice);

        Assert.Equal(new[] { "ACH_ONLY", "Game" }, presentation.BodyLines);
    }

    private static StoredAchievement Stored(string apiName, string displayName, string description)
    {
        var now = DateTimeOffset.UtcNow;
        return new StoredAchievement(
            Guid.NewGuid(),
            apiName,
            displayName,
            description,
            Hidden: false,
            IsUnlocked: true,
            UnlockedAtUtc: now,
            Source: "test",
            FirstSeenAtUtc: now,
            LastSeenAtUtc: now,
            FirstUnlockedSeenAtUtc: now);
    }
}
