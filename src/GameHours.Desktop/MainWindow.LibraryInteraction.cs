using System.Collections;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private bool _librarySortConfigured;
    private DesktopStatus? _librarySortStatus;
    private IReadOnlyDictionary<Guid, int> _librarySortRanks = new Dictionary<Guid, int>();

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_librarySortConfigured)
        {
            return;
        }

        _librarySortConfigured = true;
        if (CollectionViewSource.GetDefaultView(Games) is ListCollectionView view)
        {
            view.CustomSort = new LibraryGameViewModelComparer(this);
            view.Refresh();
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (e.Handled)
        {
            return;
        }

        var activeGame = FindDataContext<ActiveGameRowViewModel>(e.OriginalSource as DependencyObject);
        if (activeGame is null)
        {
            return;
        }

        var game = ResolveActiveGameTarget(activeGame, Games);
        if (game is null)
        {
            return;
        }

        _selectedGameId = game.GameId;
        SelectedGameDetail = BuildGameDetail(game);
        ShowSection(DesktopSection.GameDetail);
        e.Handled = true;
    }

    internal static GameRowViewModel? ResolveActiveGameTarget(
        ActiveGameRowViewModel activeGame,
        IEnumerable<GameRowViewModel> games)
    {
        ArgumentNullException.ThrowIfNull(activeGame);
        ArgumentNullException.ThrowIfNull(games);

        if (activeGame.GameId != Guid.Empty)
        {
            return games.FirstOrDefault(game => game.GameId == activeGame.GameId);
        }

        return games.FirstOrDefault(game =>
            string.Equals(game.Title, activeGame.Title, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<Guid> OrderLibraryGameIdsByRecency(
        IReadOnlyList<DesktopGameRow> games,
        IReadOnlyList<DesktopActiveGame> activeGames)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(activeGames);

        return games
            .Select(game => new
            {
                game.GameId,
                game.Title,
                ActiveStartedAtUtc = FindActiveStartedAt(game, activeGames),
                game.LastActivityAtUtc
            })
            .OrderByDescending(item => item.ActiveStartedAtUtc.HasValue)
            .ThenByDescending(item => item.ActiveStartedAtUtc ?? item.LastActivityAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.GameId)
            .ToArray();
    }

    private IReadOnlyDictionary<Guid, int> GetLibrarySortRanks()
    {
        var status = _host.CurrentStatus;
        if (ReferenceEquals(status, _librarySortStatus))
        {
            return _librarySortRanks;
        }

        _librarySortStatus = status;
        _librarySortRanks = OrderLibraryGameIdsByRecency(
                status.Games,
                ResolveActiveGames(status))
            .Select((gameId, index) => new { gameId, index })
            .ToDictionary(item => item.gameId, item => item.index);
        return _librarySortRanks;
    }

    private static DateTimeOffset? FindActiveStartedAt(
        DesktopGameRow game,
        IReadOnlyList<DesktopActiveGame> activeGames)
    {
        var byId = activeGames
            .Where(active => active.GameId != Guid.Empty && active.GameId == game.GameId)
            .Select(active => (DateTimeOffset?)active.StartedAtUtc)
            .Max();
        if (byId is not null)
        {
            return byId;
        }

        return activeGames
            .Where(active =>
                active.GameId == Guid.Empty &&
                string.Equals(active.Title, game.Title, StringComparison.OrdinalIgnoreCase))
            .Select(active => (DateTimeOffset?)active.StartedAtUtc)
            .Max();
    }

    private static T? FindDataContext<T>(DependencyObject? source)
        where T : class
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is FrameworkElement { DataContext: T dataContext })
            {
                return dataContext;
            }

            if (current is FrameworkContentElement { DataContext: T contentDataContext })
            {
                return contentDataContext;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current) =>
        current is Visual or Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private sealed class LibraryGameViewModelComparer : IComparer
    {
        private readonly MainWindow _owner;

        public LibraryGameViewModelComparer(MainWindow owner)
        {
            _owner = owner;
        }

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is not GameRowViewModel left)
            {
                return 1;
            }

            if (y is not GameRowViewModel right)
            {
                return -1;
            }

            var ranks = _owner.GetLibrarySortRanks();
            var leftRank = ranks.TryGetValue(left.GameId, out var resolvedLeft)
                ? resolvedLeft
                : int.MaxValue;
            var rightRank = ranks.TryGetValue(right.GameId, out var resolvedRight)
                ? resolvedRight
                : int.MaxValue;

            var rankComparison = leftRank.CompareTo(rightRank);
            return rankComparison != 0
                ? rankComparison
                : StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
        }
    }
}
