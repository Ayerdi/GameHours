using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class SteamCommunityAchievementPageClientTests
{
    [Fact]
    public void Parse_ReadsCurrentProtocolRelativeArtworkAndApiNameFromSteamRow()
    {
        const string html = """
            <html><body>
              <div class="achieveRow">
                <div class="achieveImgHolder">
                  <img id="iconImgach_1_button_clicks" src="//shared.akamai.steamstatic.com/community_assets/images/apps/3946950/real-icon.jpg">
                </div>
                <div class="achieveTxt">
                  <h3>¡Solo es el principio!</h3>
                  <h5>Haz clic en el botón 100 veces</h5>
                </div>
              </div>
            </body></html>
            """;

        var row = Assert.Single(SteamCommunityAchievementPageClient.Parse(html, "3946950"));

        Assert.Equal("ach_1_button_clicks", row.ApiName);
        Assert.Equal("¡Solo es el principio!", row.DisplayName);
        Assert.Equal("Haz clic en el botón 100 veces", row.Description);
        Assert.Equal(
            "https://shared.akamai.steamstatic.com/community_assets/images/apps/3946950/real-icon.jpg",
            row.IconUrl);
    }

    [Fact]
    public void Parse_AcceptsLegacyArtworkButRejectsAnotherAppOrHost()
    {
        const string html = """
            <div class="achieveRow">
              <img id="iconImglegacy" src="https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/3946950/legacy.jpg">
              <h3>Legacy</h3>
            </div>
            <div class="achieveRow">
              <img id="iconImgach_one" src="https://example.com/community_assets/images/apps/3946950/icon.jpg">
              <h3>One</h3>
            </div>
            <div class="achieveRow">
              <img id="iconImgach_two" src="https://shared.akamai.steamstatic.com/community_assets/images/apps/999/icon.jpg">
              <h3>Two</h3>
            </div>
            """;

        var row = Assert.Single(SteamCommunityAchievementPageClient.Parse(html, "3946950"));
        Assert.Equal("legacy", row.ApiName);
        Assert.EndsWith("/legacy.jpg", row.IconUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyArtwork_PrefersApiNameAndDropsStaleLockedArtwork()
    {
        var metadata = new[]
        {
            new SteamAchievementMetadata(
                "ach_1_button_clicks",
                "¡Solo es el principio!",
                "Haz clic en el botón 100 veces",
                false,
                "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/3946950/stale.jpg",
                "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/3946950/stale-gray.jpg")
        };
        var rows = new[]
        {
            new SteamCommunityAchievementRow(
                "ACH_1_BUTTON_CLICKS",
                "Different localized title",
                string.Empty,
                "https://shared.akamai.steamstatic.com/community_assets/images/apps/3946950/real.jpg")
        };

        var result = SteamCommunityAchievementPageClient.ApplyArtwork(metadata, rows);
        var achievement = Assert.Single(result);

        Assert.EndsWith("/real.jpg", achievement.IconUrl, StringComparison.Ordinal);
        Assert.Null(achievement.LockedIconUrl);
        Assert.Equal("¡Solo es el principio!", achievement.DisplayName);
        Assert.Equal("Haz clic en el botón 100 veces", achievement.Description);
    }

    [Fact]
    public void ApplyArtwork_UsesExactLocalizedTitleBeforePositionalFallback()
    {
        var metadata = new[]
        {
            new SteamAchievementMetadata("ach_a", "Primero", "", false, "old-a", "old-a-gray"),
            new SteamAchievementMetadata("ach_b", "Segundo", "", false, "old-b", "old-b-gray")
        };
        var rows = new[]
        {
            new SteamCommunityAchievementRow(null, "Segundo", "", "https://shared.akamai.steamstatic.com/community_assets/images/apps/3946950/b.jpg"),
            new SteamCommunityAchievementRow(null, "Primero", "", "https://shared.akamai.steamstatic.com/community_assets/images/apps/3946950/a.jpg")
        };

        var result = SteamCommunityAchievementPageClient.ApplyArtwork(metadata, rows);

        Assert.EndsWith("/a.jpg", result[0].IconUrl, StringComparison.Ordinal);
        Assert.EndsWith("/b.jpg", result[1].IconUrl, StringComparison.Ordinal);
    }
}
