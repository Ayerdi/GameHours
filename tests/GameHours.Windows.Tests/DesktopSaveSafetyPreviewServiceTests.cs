using GameHours.Core.Domain;
using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class DesktopSaveSafetyPreviewServiceTests
{
    [Fact]
    public void TryCreateEngineRequest_UsesSteamAppIdAndLibraryRoot()
    {
        var install = Path.Combine("D:\\Games", "steamapps", "common", "Example Game");

        var supported = DesktopSaveSafetyPreviewService.TryCreateEngineRequest(
            GameDiscoverySource.Steam,
            "12345",
            install,
            out var identity,
            out var root,
            out var reason);

        Assert.True(supported, reason);
        Assert.Equal("steam", identity!.Store);
        Assert.Equal("12345", identity.ExternalId);
        Assert.Equal(Path.GetFullPath("D:\\Games"), root!.Path);
        Assert.Equal("steam", root.Store);
    }

    [Fact]
    public void TryCreateEngineRequest_UsesGogIdAndParentLibraryRoot()
    {
        var install = Path.Combine("D:\\GOG Games", "Example Game");

        var supported = DesktopSaveSafetyPreviewService.TryCreateEngineRequest(
            GameDiscoverySource.Gog,
            "98765",
            install,
            out var identity,
            out var root,
            out var reason);

        Assert.True(supported, reason);
        Assert.Equal("gog", identity!.Store);
        Assert.Equal("98765", identity.ExternalId);
        Assert.Equal(Path.GetFullPath("D:\\GOG Games"), root!.Path);
        Assert.Equal("gog", root.Store);
    }

    [Fact]
    public void TryCreateEngineRequest_DoesNotGuessEpicByTitle()
    {
        var supported = DesktopSaveSafetyPreviewService.TryCreateEngineRequest(
            GameDiscoverySource.Epic,
            "catalog-id",
            "D:\\Epic\\Example Game",
            out var identity,
            out var root,
            out var reason);

        Assert.False(supported);
        Assert.Null(identity);
        Assert.Null(root);
        Assert.Contains("no se adivina por título", reason, StringComparison.OrdinalIgnoreCase);
    }
}
