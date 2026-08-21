using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class SteamLibraryCacheAchievementReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    public SteamLibraryCacheAchievementReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TryReadCacheFile_ParsesSteamPairArrayShape()
    {
        var path = Path.Combine(_root, "123456.json");
        File.WriteAllText(path, """
            [
              ["something", {"data": {}}],
              ["achievements", {
                "data": {
                  "vecHighlight": [
                    {"strID":"ACH_ONE","bAchieved":true,"rtUnlocked":1700000000},
                    {"strID":"ACH_TWO","bAchieved":false,"rtUnlocked":0}
                  ]
                }
              }]
            ]
            """);

        var snapshot = new SteamLibraryCacheAchievementReader()
            .TryReadCacheFile(path, "123456");

        Assert.NotNull(snapshot);
        Assert.Equal("123456", snapshot.AppId);
        Assert.Contains("estado parcial", snapshot.Source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, snapshot.Achievements.Count);
        Assert.Equal(1, snapshot.UnlockedCount);

        var unlocked = Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_ONE");
        Assert.True(unlocked.IsUnlocked);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), unlocked.UnlockedAtUtc);

        var locked = Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_TWO");
        Assert.False(locked.IsUnlocked);
        Assert.Null(locked.UnlockedAtUtc);
    }

    [Fact]
    public void TryReadCacheFile_ParsesObjectShapeAndRejectsUnrelatedJson()
    {
        var validPath = Path.Combine(_root, "654321.json");
        File.WriteAllText(validPath, """
            {
              "achievements": {
                "data": {
                  "vecHighlight": [
                    {"strID":"ACH_OBJECT","bAchieved":1,"rtUnlocked":"1700000123"}
                  ]
                }
              }
            }
            """);

        var invalidPath = Path.Combine(_root, "invalid.json");
        File.WriteAllText(invalidPath, "{\"data\":[]}");

        var reader = new SteamLibraryCacheAchievementReader();
        var valid = reader.TryReadCacheFile(validPath);
        var invalid = reader.TryReadCacheFile(invalidPath);

        Assert.NotNull(valid);
        Assert.Equal("654321", valid.AppId);
        Assert.Equal(1, valid.UnlockedCount);
        Assert.Null(invalid);
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
