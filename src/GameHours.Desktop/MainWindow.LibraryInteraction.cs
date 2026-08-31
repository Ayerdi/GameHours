using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private bool _libraryViewConfigured;
    private bool _activeGameCursor;
    private readonly Dictionary<Guid, LibraryGamePreferences> _libraryPreferences = new();
    private readonly SemaphoreSlim _libraryPreferenceWriteGate = new(1, 1);
    private LibraryToolbar? _libraryToolbar;
    private ListCollectionView? _libraryCollectionView;
    private SqliteLibraryGamePreferencesRepository? _libraryPreferencesRepository;
    private Task? _libraryPreferencesLoadTask;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_libraryViewConfigured)
        {
            return;
        }

        _libraryViewConfigured = true;
        AttachLibraryToolbar();
        if (CollectionViewSource.GetDefaultView(Games) is ListCollectionView view)
        {
            _libraryCollectionView = view;
            view.CustomSort = new LibraryGameViewModelComparer(this);
            view.Filter = ShouldDisplayLibraryRow;
            view.Refresh();
        }

        Games.CollectionChanged += Games_CollectionChanged;
        _libraryPreferencesLoadTask = LoadLibraryPreferencesAsync();
        UpdateLibraryVisibleCount();
    }

    protected override void OnClosed(EventArgs e)
    {
        Games.CollectionChanged -= Games_CollectionChanged;
        if (_libraryToolbar is not null)
        {
            _libraryToolbar.FilterChanged -= LibraryToolbar_FilterChanged;
        }

        _libraryPreferenceWriteGate.Dispose();
        base.OnClosed(e);
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

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        if (e.Handled)
        {
            return;
        }

        var game = FindDataContext<GameRowViewModel>(e.OriginalSource as DependencyObject);
        if (game is null)
        {
            return;
        }

        var menu = BuildLibraryContextMenu(game);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
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

    private void AttachLibraryToolbar()
    {
        if (LibraryView.Child is not Grid grid || grid.RowDefinitions.Count < 3)
        {
            return;
        }

        grid.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in grid.Children.Cast<UIElement>().ToArray())
        {
            var row = Grid.GetRow(child);
            if (row >= 1)
            {
                Grid.SetRow(child, row + 1);
            }
        }

        _libraryToolbar = new LibraryToolbar();
        _libraryToolbar.FilterChanged += LibraryToolbar_FilterChanged;
        Grid.SetRow(_libraryToolbar, 1);
        grid.Children.Add(_libraryToolbar);
    }

    private async Task LoadLibraryPreferencesAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_host.DatabasePath))
            {
                return;
            }

            var database = new GameHoursDatabase(_host.DatabasePath);
            var repository = new SqliteLibraryGamePreferencesRepository(database);
            var loaded = await repository.GetAllAsync();

            _libraryPreferencesRepository = repository;
            _libraryPreferences.Clear();
            foreach (var pair in loaded)
            {
                _libraryPreferences[pair.Key] = pair.Value;
            }

            RefreshLibraryView();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "No se pudo cargar la organización de la biblioteca",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Games_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateLibraryVisibleCount();

    private void LibraryToolbar_FilterChanged()
    {
        RefreshLibraryView();
    }

    private void RefreshLibraryView()
    {
        _libraryCollectionView?.Refresh();
        UpdateLibraryVisibleCount();
    }

    private void UpdateLibraryVisibleCount()
    {
        if (_libraryToolbar is null)
        {
            return;
        }

        var visible = _libraryCollectionView?.Cast<object>().Count() ?? Games.Count;
        _libraryToolbar.SetCount(visible, Games.Count);
    }

    private bool ShouldDisplayLibraryRow(object item)
    {
        if (item is not GameRowViewModel game)
        {
            return false;
        }

        var preferences = GetLibraryPreferences(game.GameId);
        var activeGames = ResolveActiveGames(_host.CurrentStatus);
        return ShouldShowLibraryGame(
            game,
            preferences,
            _libraryToolbar?.Scope ?? LibraryFilterScope.All,
            _libraryToolbar?.SearchText,
            activeGames);
    }

    private LibraryGamePreferences GetLibraryPreferences(Guid gameId) =>
        _libraryPreferences.TryGetValue(gameId, out var preferences)
            ? preferences
            : new LibraryGamePreferences(gameId);

    private ContextMenu BuildLibraryContextMenu(GameRowViewModel game)
    {
        var preferences = GetLibraryPreferences(game.GameId);
        var menu = new ContextMenu
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush")
        };

        var favorite = new MenuItem
        {
            Header = preferences.IsFavorite ? "★ Quitar de favoritos" : "☆ Añadir a favoritos"
        };
        favorite.Click += async (_, _) => await SaveLibraryPreferencesAsync(
            preferences with { IsFavorite = !preferences.IsFavorite });
        menu.Items.Add(favorite);

        var statusMenu = new MenuItem { Header = "Estado" };
        AddCompletionStatusItem(statusMenu, game.GameId, preferences, LibraryCompletionStatus.Unspecified, "Sin estado");
        AddCompletionStatusItem(statusMenu, game.GameId, preferences, LibraryCompletionStatus.Backlog, "Pendiente");
        AddCompletionStatusItem(statusMenu, game.GameId, preferences, LibraryCompletionStatus.Playing, "Jugando");
        AddCompletionStatusItem(statusMenu, game.GameId, preferences, LibraryCompletionStatus.Completed, "Completado");
        AddCompletionStatusItem(statusMenu, game.GameId, preferences, LibraryCompletionStatus.Abandoned, "Abandonado");
        menu.Items.Add(statusMenu);

        menu.Items.Add(new Separator());
        var hidden = new MenuItem
        {
            Header = preferences.IsHidden ? "Mostrar en la biblioteca" : "Ocultar de la biblioteca"
        };
        hidden.Click += async (_, _) => await SaveLibraryPreferencesAsync(
            preferences with { IsHidden = !preferences.IsHidden });
        menu.Items.Add(hidden);

        return menu;
    }

    private void AddCompletionStatusItem(
        MenuItem parent,
        Guid gameId,
        LibraryGamePreferences current,
        LibraryCompletionStatus status,
        string label)
    {
        var item = new MenuItem
        {
            Header = label,
            IsCheckable = true,
            IsChecked = current.CompletionStatus == status
        };
        item.Click += async (_, _) => await SaveLibraryPreferencesAsync(
            current with { GameId = gameId, CompletionStatus = status });
        parent.Items.Add(item);
    }

    private async Task SaveLibraryPreferencesAsync(LibraryGamePreferences preferences)
    {
        try
        {
            if (_libraryPreferencesLoadTask is not null)
            {
                await _libraryPreferencesLoadTask;
            }

            if (_libraryPreferencesRepository is null)
            {
                throw new InvalidOperationException("El almacenamiento de la biblioteca todavía no está disponible.");
            }

            await _libraryPreferenceWriteGate.WaitAsync();
            try
            {
                await _libraryPreferencesRepository.SetAsync(preferences);
            }
            finally
            {
                _libraryPreferenceWriteGate.Release();
            }

            if (preferences.IsDefault)
            {
                _libraryPreferences.Remove(preferences.GameId);
            }
            else
            {
                _libraryPreferences[preferences.GameId] = preferences;
            }

            RefreshLibraryView();
        }
        catch (ObjectDisposedException) when (!IsLoaded)
        {
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "No se pudo guardar la organización del juego",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    internal static bool ShouldShowLibraryGame(
        GameRowViewModel game,
        LibraryGamePreferences preferences,
        LibraryFilterScope scope,
        string? searchText,
        IReadOnlyList<DesktopActiveGame> activeGames)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(activeGames);

        var hiddenScope = scope == LibraryFilterScope.Hidden;
        if (hiddenScope != preferences.IsHidden)
        {
            return false;
        }

        var matchesScope = scope switch
        {
            LibraryFilterScope.All or LibraryFilterScope.Hidden => true,
            LibraryFilterScope.Favorites => preferences.IsFavorite,
            LibraryFilterScope.Running => IsGameActive(game, activeGames),
            LibraryFilterScope.Backlog => preferences.CompletionStatus == LibraryCompletionStatus.Backlog,
            LibraryFilterScope.Playing => preferences.CompletionStatus == LibraryCompletionStatus.Playing,
            LibraryFilterScope.Completed => preferences.CompletionStatus == LibraryCompletionStatus.Completed,
            LibraryFilterScope.Abandoned => preferences.CompletionStatus == LibraryCompletionStatus.Abandoned,
            _ => false
        };

        return matchesScope && MatchesLibrarySearch(game.Title, searchText);
    }

    internal static bool MatchesLibrarySearch(string title, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var trimmed = query.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        if (compare.IndexOf(title, trimmed, options) >= 0)
        {
            return true;
        }

        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > 1 && tokens.All(token => compare.IndexOf(title, token, options) >= 0))
        {
            return true;
        }

        if (tokens.Length == 1)
        {
            var acronym = BuildTitleAcronym(title);
            return acronym.Length > 1 && compare.IsPrefix(acronym, tokens[0], options);
        }

        return false;
    }

    private static string BuildTitleAcronym(string title)
    {
        var builder = new StringBuilder();
        var atWordStart = true;
        foreach (var character in title)
        {
            if (!char.IsLetterOrDigit(character))
            {
                atWordStart = true;
                continue;
            }

            if (atWordStart)
            {
                builder.Append(character);
                atWordStart = false;
            }
        }

        return builder.ToString();
    }

    private static bool IsGameActive(
        GameRowViewModel game,
        IReadOnlyList<DesktopActiveGame> activeGames) =>
        activeGames.Any(active =>
            active.GameId != Guid.Empty
                ? active.GameId == game.GameId
                : string.Equals(active.Title, game.Title, StringComparison.OrdinalIgnoreCase));

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
