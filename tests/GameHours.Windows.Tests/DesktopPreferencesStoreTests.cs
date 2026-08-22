using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class DesktopPreferencesStoreTests
{
    [Fact]
    public void MissingFile_UsesSafeDefaults()
    {
        using var temp = new TemporaryDirectory();
        var store = new DesktopPreferencesStore(Path.Combine(temp.Path, "settings.json"));

        Assert.Equal(DesktopPreferences.Default, store.Current);
        Assert.True(store.Current.LowImpactMode);
        Assert.Equal(5, store.Current.AfkTimeoutMinutes);
    }

    [Fact]
    public void SaveAndReload_RoundTripsAndReplacesExistingFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new DesktopPreferencesStore(path);

        store.Save(new DesktopPreferences(2, false));
        store.Save(new DesktopPreferences(10, true));

        var reloaded = new DesktopPreferencesStore(path).Current;
        Assert.Equal(new DesktopPreferences(10, true), reloaded);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void UnsupportedAfkTimeout_NormalizesToRecommendedDefault()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var store = new DesktopPreferencesStore(path);

        store.Save(new DesktopPreferences(7, false));

        Assert.Equal(5, new DesktopPreferencesStore(path).Current.AfkTimeoutMinutes);
    }

    [Fact]
    public void CorruptJson_FallsBackToDefaultsWithoutRewritingUserFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{ definitely not json }");
        var original = File.ReadAllText(path);

        var preferences = new DesktopPreferencesStore(path).Current;

        Assert.Equal(DesktopPreferences.Default, preferences);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void DisabledAfkFilter_ProducesZeroThreshold()
    {
        var preferences = new DesktopPreferences(0, true);

        Assert.False(preferences.AfkFilterEnabled);
        Assert.Equal(TimeSpan.Zero, preferences.IdleThreshold);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"gamehours-preferences-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
