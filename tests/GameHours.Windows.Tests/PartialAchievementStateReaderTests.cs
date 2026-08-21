using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class PartialAchievementStateReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public PartialAchievementStateReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TryRead_ParsesCodexRuneStyleIni()
    {
        var path = Path.Combine(_root, "achievements.ini");
        File.WriteAllText(path, """
            [ACH_FIRST]
            Achieved=1
            UnlockTime=1700000000

            [ACH_LOCKED]
            Achieved=0
            UnlockTime=0
            """);

        var snapshot = new PartialAchievementStateReader().TryRead(new LocalAchievementSourceCandidate(
            LocalAchievementSourceKind.Codex,
            path,
            "123456",
            "test"));

        Assert.NotNull(snapshot);
        Assert.Contains("estado parcial", snapshot.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("123456", snapshot.AppId);
        var achievement = Assert.Single(snapshot.Achievements);
        Assert.Equal("ACH_FIRST", achievement.ApiName);
        Assert.True(achievement.IsUnlocked);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), achievement.UnlockedAtUtc);
    }

    [Fact]
    public void TryRead_ParsesOnlineFixAndSkidrowShapes()
    {
        var onlineFixPath = Path.Combine(_root, "onlinefix.ini");
        File.WriteAllText(onlineFixPath, """
            [ACH_ONLINE]
            achieved=true
            timestamp=1700000100
            """);

        var skidrowPath = Path.Combine(_root, "achiev.ini");
        File.WriteAllText(skidrowPath, """
            [Achievements]
            ACH_SKID=1@anything@1700000200
            ACH_LOCKED=0@anything@0
            """);

        var reader = new PartialAchievementStateReader();
        var online = reader.TryRead(new LocalAchievementSourceCandidate(
            LocalAchievementSourceKind.OnlineFix,
            onlineFixPath,
            "1",
            "test"));
        var skidrow = reader.TryRead(new LocalAchievementSourceCandidate(
            LocalAchievementSourceKind.Skidrow,
            skidrowPath,
            "2",
            "test"));

        Assert.NotNull(online);
        Assert.Equal("ACH_ONLINE", Assert.Single(online.Achievements).ApiName);
        Assert.NotNull(skidrow);
        Assert.Equal("ACH_SKID", Assert.Single(skidrow.Achievements).ApiName);
    }

    [Fact]
    public void TryRead_ParsesGoldbergLikeEmpressJson()
    {
        var path = Path.Combine(_root, "achievements.json");
        File.WriteAllText(path, """
            {
              "ACH_ONE": { "earned": true, "earned_time": 1700000300 },
              "ACH_TWO": { "earned": false, "earned_time": 0 }
            }
            """);

        var snapshot = new PartialAchievementStateReader().TryRead(new LocalAchievementSourceCandidate(
            LocalAchievementSourceKind.Empress,
            path,
            "42",
            "test"));

        Assert.NotNull(snapshot);
        var achievement = Assert.Single(snapshot.Achievements);
        Assert.Equal("ACH_ONE", achievement.ApiName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000300), achievement.UnlockedAtUtc);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }
}
