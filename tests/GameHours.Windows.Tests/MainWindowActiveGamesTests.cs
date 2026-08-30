using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class MainWindowActiveGamesTests
{
    [Fact]
    public void ResolveActiveGames_PreservesEveryGameAndOrdersByStart()
    {
        var later = new DesktopActiveGame(
            Guid.NewGuid(),
            "Slay the Spire",
            new DateTimeOffset(2026, 8, 24, 17, 0, 30, TimeSpan.Zero));
        var earlier = new DesktopActiveGame(
            Guid.NewGuid(),
            "Balatro",
            new DateTimeOffset(2026, 8, 24, 17, 0, 0, TimeSpan.Zero));
        var status = new DesktopStatus(
            true,
            "2 juegos en ejecución",
            earlier.Title,
            earlier.StartedAtUtc,
            Array.Empty<DesktopGameRow>(),
            Array.Empty<DesktopTimelineRow>(),
            new[] { later, earlier });

        var resolved = MainWindow.ResolveActiveGames(status);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(earlier.GameId, resolved[0].GameId);
        Assert.Equal(later.GameId, resolved[1].GameId);
    }

    [Fact]
    public void ResolveActiveGames_FallsBackToLegacySingleGameStatus()
    {
        var startedAt = new DateTimeOffset(2026, 8, 24, 17, 0, 0, TimeSpan.Zero);
        var status = new DesktopStatus(
            true,
            "Jugando a Balatro",
            "Balatro",
            startedAt,
            Array.Empty<DesktopGameRow>(),
            Array.Empty<DesktopTimelineRow>());

        var active = Assert.Single(MainWindow.ResolveActiveGames(status));

        Assert.Equal("Balatro", active.Title);
        Assert.Equal(startedAt, active.StartedAtUtc);
    }

    [Fact]
    public void ActiveGameRows_KeepIndependentElapsedTimes()
    {
        var now = new DateTimeOffset(2026, 8, 24, 17, 10, 0, TimeSpan.Zero);
        var first = new MainWindow.ActiveGameRowViewModel(new DesktopActiveGame(
            Guid.NewGuid(),
            "Balatro",
            now.AddMinutes(-10)));
        var second = new MainWindow.ActiveGameRowViewModel(new DesktopActiveGame(
            Guid.NewGuid(),
            "Slay the Spire",
            now.AddMinutes(-2).AddSeconds(-5)));

        first.UpdateElapsed(now);
        second.UpdateElapsed(now);

        Assert.Equal("10:00", first.ElapsedText);
        Assert.Equal("02:05", second.ElapsedText);
    }

    [Theory]
    [InlineData(10, 28, true, "10/28")]
    [InlineData(4, 4, false, "4/?")]
    [InlineData(0, 42, true, "0/42")]
    public void AchievementCount_UsesCatalogueTotalOnlyWhenKnown(
        int unlocked,
        int known,
        bool completeCatalogue,
        string expected)
    {
        Assert.Equal(
            expected,
            MainWindow.FormatAchievementCount(unlocked, known, completeCatalogue));
    }

    [Fact]
    public void AchievementCount_NoObservedStateShowsDash()
    {
        Assert.Equal("—", MainWindow.FormatAchievementCount(null, null, false));
    }

    [Fact]
    public void NowCard_BindsToConcurrentActiveGameCollection()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GameHours.Desktop",
            "MainWindow.xaml"));

        Assert.Contains("ItemsSource=\"{Binding ActiveGames}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ElapsedText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"118\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryRows_BindAchievementSummaryColumn()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GameHours.Desktop",
            "MainWindow.xaml"));

        Assert.Contains("Text=\"LOGROS\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding AchievementText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Logros desbloqueados / total conocido", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameHours.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the GameHours repository root.");
    }
}
