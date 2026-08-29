using System.Text.Json;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class SteamGlobalAchievementNameClientTests
{
    [Fact]
    public void ParseNamesReadsAchievementApiNamesAndDeduplicatesCaseInsensitively()
    {
        using var document = JsonDocument.Parse("""
            {
              "achievementpercentages": {
                "achievements": [
                  { "name": "ACH_FIRST", "percent": 88.5 },
                  { "name": "ACH_SECOND", "percent": 0.0 },
                  { "name": "ach_first", "percent": 88.5 },
                  { "name": "   ", "percent": 1.0 }
                ]
              }
            }
            """);

        var names = SteamGlobalAchievementNameClient.ParseNames(document.RootElement);

        Assert.Equal(2, names.Count);
        Assert.Equal("ACH_FIRST", names[0]);
        Assert.Equal("ACH_SECOND", names[1]);
    }

    [Fact]
    public void ParseNamesReturnsEmptyForUnexpectedPayload()
    {
        using var document = JsonDocument.Parse("{\"response\":{}}");

        var names = SteamGlobalAchievementNameClient.ParseNames(document.RootElement);

        Assert.Empty(names);
    }
}
