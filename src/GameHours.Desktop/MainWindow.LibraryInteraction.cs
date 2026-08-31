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
    private bool _activeGameCursor;

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

    protected override void OnPreviewMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        var overActiveGame = FindDataContext<ActiveGameRowViewModel>(
            e.OriginalSource as DependencyObject) is not null;
        if (overActiveGame == _activeGameCursor)
        {
            return;
        }

        _activeGameCursor = overActiveGame;
        Cursor = overActiveGame ? System.Windows.Input.Cursors.Hand : null;
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _activeGameCursor = false;
        Cursor = null;
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

        var ordered = games.ToArray();
        Array.Sort(
            ordered,
            (left, right) => CompareLibraryGames(left, right, activeGames));
        return ordered.Select(game => game.GameId).ToArray();
    }

    private static int CompareLibraryGames(
        DesktopGameRow left,
        DesktopGameRow right,
        IReadOnlyList<DesktopActiveGame> activeGames)
    {
        var leftActive = FindActiveStartedAt(left, activeGames);
        var rightActive = FindActiveStartedAt(right, activeGames);
        var activeComparison = rightActive.HasValue.CompareTo(leftActive.HasValue);
        if (activeComparison != 0)
        {
            return activeComparison;
        }

        var leftRecency = leftActive ?? left.LastActivityAtUtc ?? DateTimeOffset.MinValue;
        var rightRecency = rightActive ?? right.LastActivityAtUtc ?? DateTimeOffset.MinValue;
        var recencyComparison = rightRecency.CompareTo(leftRecency);
        return recencyComparison != 0
            ? recencyComparison
            : StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
    }

    private static DateTimeOffset? FindActiveStartedAt(
        DesktopGameRow game,
        IReadOnlyList<DesktopActiveGame> activeGames)
    {
        var matching = activeGames.Where(active =>
            active.GameId != Guid.Empty
                ? active.GameId == game.GameId
                : string.Equals(active.Title, game.Title, StringComparison.OrdinalIgnoreCase));
        return matching.Any()
            ? matching.Max(active => active.StartedAtUtc)
            : null;
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

            if (x is not GameRowViewModel left || y is not GameRowViewModel right)
            {
                return x is null ? 1 : y is null ? -1 : 0;
            }

            var status = _owner._host.CurrentStatus;
            var leftGame = status.Games.FirstOrDefault(game => game.GameId == left.GameId);
            var rightGame = status.Games.FirstOrDefault(game => game.GameId == right.GameId);
            if (leftGame is null || rightGame is null)
            {
                return StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
            }

            return CompareLibraryGames(leftGame, rightGame, ResolveActiveGames(status));
        }
    }
}
