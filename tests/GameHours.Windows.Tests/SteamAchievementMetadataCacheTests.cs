using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class SteamAchievementMetadataCacheTests
{
    [Fact]
    public void ParseOfficialResponse_MapsLocalizedMetadataAndTrustedArtwork()
    {
        const string json = """
            {
              "response": {
                "achievements": [
                  {
                    "internal_name": "ach_click_once",
                    "localized_name": "Primer clic",
                    "localized_desc": "Pulsa el botón una vez.",
                    "icon": "0123456789abcdef0123456789abcdef01234567",
                    "icon_gray": "89abcdef0123456789abcdef0123456789abcdef",
                    "hidden": false,
                    "player_percent_unlocked": 72.5
                  }
                ]
              }
            }
            """;

        var metadata = SteamAchievementMetadataCache.ParseOfficialResponse(json, "3946950");

        var achievement = Assert.Single(metadata);
        Assert.Equal("ach_click_once", achievement.ApiName);
        Assert.Equal("Primer clic", achievement.DisplayName);
        Assert.Equal("Pulsa el botón una vez.", achievement.Description);
        Assert.False(achievement.Hidden);
        Assert.Equal(
            "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/0123456789abcdef0123456789abcdef01234567",
            achievement.IconUrl);
        Assert.Equal(
            "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/89abcdef0123456789abcdef0123456789abcdef",
            achievement.LockedIconUrl);
    }

    [Fact]
    public void Enrich_MatchesApiNameCaseInsensitively_AndPreservesLocalState()
    {
        var unlockedAt = new DateTimeOffset(2026, 8, 29, 15, 52, 0, TimeSpan.Zero);
        var snapshot = new LocalAchievementSnapshot(
            "GSE/Goldberg local",
            "3946950",
            @"D:\Games\Click the Button\steam_settings\achievements.json",
            @"C:\Users\test\AppData\Roaming\GSE Saves\3946950\achievements.json",
            new[]
            {
                new LocalAchievement(
                    "ACH_CLICK_ONCE",
                    "ACH_CLICK_ONCE",
                    string.Empty,
                    false,
                    true,
                    unlockedAt,
                    null,
                    null,
                    7,
                    10)
            });
        var metadata = new[]
        {
            new SteamAchievementMetadata(
                "ach_click_once",
                "Primer clic",
                "Pulsa el botón una vez.",
                true,
                "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/unlocked.jpg",
                "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/locked.jpg")
        };

        var enriched = SteamAchievementMetadataCache.Enrich(snapshot, metadata);

        var achievement = Assert.Single(enriched.Achievements);
        Assert.Equal("ACH_CLICK_ONCE", achievement.ApiName);
        Assert.Equal("Primer clic", achievement.DisplayName);
        Assert.Equal("Pulsa el botón una vez.", achievement.Description);
        Assert.True(achievement.Hidden);
        Assert.True(achievement.IsUnlocked);
        Assert.Equal(unlockedAt, achievement.UnlockedAtUtc);
        Assert.Equal(7, achievement.Progress);
        Assert.Equal(10, achievement.MaxProgress);
        Assert.EndsWith("/unlocked.jpg", achievement.IconPath, StringComparison.Ordinal);
        Assert.EndsWith("/locked.jpg", achievement.LockedIconPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Enrich_DoesNotAddOrCrossMatchUnrelatedAchievements()
    {
        var snapshot = new LocalAchievementSnapshot(
            "GSE/Goldberg local",
            "3946950",
            "definitions.json",
            "state.json",
            new[]
            {
                new LocalAchievement(
                    "ach_local",
                    "ach_local",
                    string.Empty,
                    false,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null)
            });
        var metadata = new[]
        {
            new SteamAchievementMetadata(
                "ach_other",
                "Otro logro",
                "No pertenece al estado local observado.",
                false,
                "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/other.jpg",
                null)
        };

        var enriched = SteamAchievementMetadataCache.Enrich(snapshot, metadata);

        var achievement = Assert.Single(enriched.Achievements);
        Assert.Equal("ach_local", achievement.ApiName);
        Assert.Equal("ach_local", achievement.DisplayName);
        Assert.Null(achievement.IconPath);
    }

    [Fact]
    public void ParseOfficialResponse_RejectsMissingAchievementCollection()
    {
        var metadata = SteamAchievementMetadataCache.ParseOfficialResponse(
            "{\"response\":{\"game_name\":\"Click the Button\"}}",
            "3946950");

        Assert.Empty(metadata);
    }
}
