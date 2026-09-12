using GameHours.Core.Domain;
using GameHours.Desktop;
using GameHours.SaveSafety;

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

    [Fact]
    public void BuildReadyDetail_TreatsFileCountAsBackupPayloadNotSaveCount()
    {
        var preview = new SaveDataPreview(
            "Baldur's Gate 3",
            FileCount: 666,
            TotalBytes: 7_164_873_034,
            RegistryKeyCount: 0,
            Files: [new SaveDataFile("save.lsv", 10, Ignored: false, Failed: false)],
            RegistryKeys: []);

        var detail = DesktopSaveSafetyPreviewService.BuildReadyDetail(preview);

        Assert.Contains("666 archivos asociados", detail, StringComparison.Ordinal);
        Assert.Contains("no equivale al número de partidas", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("666 partidas", detail, StringComparison.OrdinalIgnoreCase);
    }
}
