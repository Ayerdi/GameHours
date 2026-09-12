using GameHours.Core.Domain;
using GameHours.Desktop;
using GameHours.SaveSafety;
using GameHours.Storage.Sqlite;

namespace GameHours.Windows.Tests;

public sealed class DesktopSaveSafetyServiceTests
{
    [Fact]
    public void TryCreateEngineRequest_UsesSteamAppIdAndLibraryRoot()
    {
        var install = Path.Combine("D:\\Games", "steamapps", "common", "Example Game");

        var supported = DesktopSaveSafetyService.TryCreateEngineRequest(
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

        var supported = DesktopSaveSafetyService.TryCreateEngineRequest(
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
        var supported = DesktopSaveSafetyService.TryCreateEngineRequest(
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
            RegistryKeys: [],
            Selection: new SaveDataSelection(
                SaveDataScope.PortableSave,
                SaveFilterApplied: true,
                RetainedUnclassifiedEntries: false,
                ExcludedConfigEntries: 2));

        var detail = DesktopSaveSafetyService.BuildReadyDetail(preview);

        Assert.Contains("666 archivos protegibles", detail, StringComparison.Ordinal);
        Assert.Contains("omite las entradas", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no equivale al número de partidas", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("666 partidas", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReadyDetail_ExplainsFallbackWhenManifestCannotSeparateSaveAndConfig()
    {
        var preview = new SaveDataPreview(
            "Legacy Game",
            FileCount: 12,
            TotalBytes: 4_096,
            RegistryKeyCount: 0,
            Files: [new SaveDataFile("legacy.dat", 4_096, Ignored: false, Failed: false)],
            RegistryKeys: [],
            Selection: new SaveDataSelection(
                SaveDataScope.PortableSave,
                SaveFilterApplied: false,
                RetainedUnclassifiedEntries: true,
                ExcludedConfigEntries: 0));

        var detail = DesktopSaveSafetyService.BuildReadyDetail(preview);

        Assert.Contains("no separa con suficiente precisión", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conserva todos los datos asociados", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12 partidas", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatPersistedState_ShowsSuccessfulPayloadWithoutPaths()
    {
        var attempt = new DateTimeOffset(2026, 9, 12, 19, 15, 0, TimeSpan.Zero);
        var state = new SaveSafetyState(
            Guid.NewGuid(),
            attempt,
            attempt,
            SaveSafetyOperationStatus.Success,
            null,
            LastFileCount: 666,
            LastTotalBytes: 7_164_873_034,
            LastChanged: true,
            UpdatedAtUtc: attempt);

        var text = DesktopSaveSafetyService.FormatPersistedState(state);

        Assert.Contains("Última copia correcta", text, StringComparison.Ordinal);
        Assert.Contains("666 archivos protegibles", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("save-safety", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatPersistedState_DistinguishesNoChangesAndFailure()
    {
        var attempt = new DateTimeOffset(2026, 9, 12, 19, 15, 0, TimeSpan.Zero);
        var unchanged = new SaveSafetyState(
            Guid.NewGuid(), attempt, attempt, SaveSafetyOperationStatus.Success,
            null, 2, 42, false, attempt);
        var failed = new SaveSafetyState(
            Guid.NewGuid(), attempt, attempt.AddHours(-1), SaveSafetyOperationStatus.Failed,
            "BackupFailure", null, null, null, attempt);

        Assert.Contains("no había cambios", DesktopSaveSafetyService.FormatPersistedState(unchanged), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BackupFailure", DesktopSaveSafetyService.FormatPersistedState(failed), StringComparison.Ordinal);
    }
}
