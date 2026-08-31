using GameHours.Core.Domain;
using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class MainWindowLibraryInteractionTests
{
    [Fact]
    public void LibraryOrder_PutsRunningGameFirstThenMostRecentActivity()
    {
        var oldFavorite = CreateGame(
            "Old Favorite",
            new DateTimeOffset(2026, 8, 1, 18, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(200));
        var recent = CreateGame(
            "Recent Game",
            new DateTimeOffset(2026, 8, 30, 18, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(2));
        var active = CreateGame(
            "Running Game",
            new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(30));
        var activeGames = new[]
        {
            new DesktopActiveGame(
                active.GameId,
                active.Title,
                new DateTimeOffset(2026, 8, 31, 18, 30, 0, TimeSpan.Zero))
        };

        var ordered = MainWindow.OrderLibraryGameIdsByRecency(
            new[] { oldFavorite, active, recent },
            activeGames);

        Assert.Equal(new[] { active.GameId, recent.GameId, oldFavorite.GameId }, ordered);
    }

    [Fact]
    public void LibraryOrder_UsesLatestStartWhenSeveralGamesAreRunning()
    {
        var earlier = CreateGame("Earlier", null, TimeSpan.Zero);
        var later = CreateGame("Later", null, TimeSpan.Zero);
        var activeGames = new[]
        {
            new DesktopActiveGame(
                earlier.GameId,
                earlier.Title,
                new DateTimeOffset(2026, 8, 31, 17, 0, 0, TimeSpan.Zero)),
            new DesktopActiveGame(
                later.GameId,
                later.Title,
                new DateTimeOffset(2026, 8, 31, 18, 0, 0, TimeSpan.Zero))
        };

        var ordered = MainWindow.OrderLibraryGameIdsByRecency(
            new[] { earlier, later },
            activeGames);

        Assert.Equal(new[] { later.GameId, earlier.GameId }, ordered);
    }

    [Theory]
    [InlineData("Pokémon Café ReMix", "pokemon cafe")]
    [InlineData("Gothic 1 Remake", "g1r")]
    [InlineData("Click.the.Button", "ctb")]
    [InlineData("The Elder Scrolls V", "elder scrolls")]
    public void LibrarySearch_IsCaseAccentAndAcronymFriendly(string title, string query)
    {
        Assert.True(MainWindow.MatchesLibrarySearch(title, query));
    }

    [Fact]
    public void DefaultLibraryScope_ExcludesHiddenGames()
    {
        var game = new MainWindow.GameRowViewModel(CreateGame("Hidden", null, TimeSpan.Zero));
        var preferences = new LibraryGamePreferences(game.GameId, IsHidden: true);

        Assert.False(MainWindow.ShouldShowLibraryGame(
            game,
            preferences,
            LibraryFilterScope.All,
            searchText: null,
            Array.Empty<DesktopActiveGame>()));
        Assert.True(MainWindow.ShouldShowLibraryGame(
            game,
            preferences,
            LibraryFilterScope.Hidden,
            searchText: null,
            Array.Empty<DesktopActiveGame>()));
    }

    [Fact]
    public void LibraryScopes_FilterFavoriteCompletionAndRunningIndependently()
    {
        var game = new MainWindow.GameRowViewModel(CreateGame("Scoped", null, TimeSpan.Zero));
        var preferences = new LibraryGamePreferences(
            game.GameId,
            IsFavorite: true,
            CompletionStatus: LibraryCompletionStatus.Playing);
        var active = new[]
        {
            new DesktopActiveGame(game.GameId, game.Title, DateTimeOffset.UtcNow)
        };

        Assert.True(MainWindow.ShouldShowLibraryGame(game, preferences, LibraryFilterScope.Favorites, null, active));
        Assert.True(MainWindow.ShouldShowLibraryGame(game, preferences, LibraryFilterScope.Playing, null, active));
        Assert.True(MainWindow.ShouldShowLibraryGame(game, preferences, LibraryFilterScope.Running, null, active));
        Assert.False(MainWindow.ShouldShowLibraryGame(game, preferences, LibraryFilterScope.Completed, null, active));
    }

    [Fact]
    public void PausedStatus_HasItsOwnCompatibleScope()
    {
        var game = new MainWindow.GameRowViewModel(CreateGame("Paused", null, TimeSpan.Zero));
        var preferences = new LibraryGamePreferences(
            game.GameId,
            CompletionStatus: LibraryCompletionStatus.Paused);

        Assert.True(MainWindow.ShouldShowLibraryGame(
            game,
            preferences,
            LibraryFilterScope.Paused,
            null,
            Array.Empty<DesktopActiveGame>()));
        Assert.False(MainWindow.ShouldShowLibraryGame(
            game,
            preferences,
            LibraryFilterScope.Playing,
            null,
            Array.Empty<DesktopActiveGame>()));
    }

    [Fact]
    public void LibrarySearch_ComposesWithScope()
    {
        var game = new MainWindow.GameRowViewModel(CreateGame("Gothic 1 Remake", null, TimeSpan.Zero));
        var preferences = new LibraryGamePreferences(game.GameId, IsFavorite: true);

        Assert.True(MainWindow.ShouldShowLibraryGame(
            game,
            preferences,
            LibraryFilterScope.Favorites,
            "gothic",
            Array.Empty<DesktopActiveGame>()));
        Assert.False(MainWindow.ShouldShowLibraryGame(
            game,
            preferences,
            LibraryFilterScope.Favorites,
            "witcher",
            Array.Empty<DesktopActiveGame>()));
    }

    [Fact]
    public void ActiveGameClickTarget_ResolvesLibraryRowByStableGameId()
    {
        var first = new MainWindow.GameRowViewModel(CreateGame("Same-ish", null, TimeSpan.Zero));
        var target = new MainWindow.GameRowViewModel(CreateGame("Target", null, TimeSpan.Zero));
        var active = new MainWindow.ActiveGameRowViewModel(new DesktopActiveGame(
            target.GameId,
            "Different display title",
            DateTimeOffset.UtcNow));

        var resolved = MainWindow.ResolveActiveGameTarget(active, new[] { first, target });

        Assert.Same(target, resolved);
    }

    [Fact]
    public void LegacyActiveGameClickTarget_FallsBackToTitle()
    {
        var target = new MainWindow.GameRowViewModel(CreateGame("Legacy Game", null, TimeSpan.Zero));
        var active = new MainWindow.ActiveGameRowViewModel(new DesktopActiveGame(
            Guid.Empty,
            "legacy game",
            DateTimeOffset.UtcNow));

        var resolved = MainWindow.ResolveActiveGameTarget(active, new[] { target });

        Assert.Same(target, resolved);
    }

    private static DesktopGameRow CreateGame(
        string title,
        DateTimeOffset? lastActivityAtUtc,
        TimeSpan totalPlaytime)
    {
        var gameId = Guid.NewGuid();
        return new DesktopGameRow(
            gameId,
            title,
            totalPlaytime,
            totalPlaytime,
            TimeSpan.Zero,
            TimeSpan.Zero,
            ActivePlaytime: null,
            ActivityMeasuredSessionCount: 0,
            FirstActivityAtUtc: lastActivityAtUtc,
            LastActivityAtUtc: lastActivityAtUtc,
            FirstMeasuredSessionAtUtc: lastActivityAtUtc,
            LastMeasuredSessionAtUtc: lastActivityAtUtc,
            MeasuredSessionCount: lastActivityAtUtc is null ? 0 : 1,
            ExecutablePath: null,
            RecentSessions: Array.Empty<DesktopActivityRow>());
    }
}
