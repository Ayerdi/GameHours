using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GameHours.Core.Domain;

namespace GameHours.Desktop;

public partial class LibraryOrganizerView : UserControl
{
    public sealed record StatusOption(LibraryCompletionStatus Status, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record OrganizerItem(
        Guid GameId,
        string Title,
        string Initial,
        ImageSource? Icon,
        string LastActivityText,
        string AchievementText,
        string TotalText,
        bool IsFavorite,
        bool IsHidden,
        LibraryCompletionStatus CompletionStatus)
    {
        public IReadOnlyList<StatusOption> StatusOptions => CompletionStatuses;
        public string FavoriteGlyph => IsFavorite ? "★" : "☆";
        public string FavoriteActionText => IsFavorite ? "Quitar de favoritos" : "Añadir a favoritos";
        public string VisibilityActionText => IsHidden ? "Mostrar" : "Ocultar";
    }

    private static readonly IReadOnlyList<StatusOption> CompletionStatuses = new[]
    {
        new StatusOption(LibraryCompletionStatus.Unspecified, "Sin estado"),
        new StatusOption(LibraryCompletionStatus.Backlog, "Pendiente"),
        new StatusOption(LibraryCompletionStatus.Playing, "Jugando"),
        new StatusOption(LibraryCompletionStatus.Paused, "Pausado"),
        new StatusOption(LibraryCompletionStatus.Completed, "Completado"),
        new StatusOption(LibraryCompletionStatus.Abandoned, "Abandonado")
    };

    private readonly ObservableCollection<OrganizerItem> _items = new();
    private ICollectionView? _view;

    public event EventHandler? BackRequested;

    public Func<Guid, LibraryCompletionStatus, Task>? CompletionStatusChangeRequestedAsync { get; set; }
    public Func<Guid, Task>? FavoriteToggleRequestedAsync { get; set; }
    public Func<Guid, Task>? VisibilityToggleRequestedAsync { get; set; }

    public LibraryOrganizerView()
    {
        InitializeComponent();
        OrganizerItems.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        if (_view is not null)
        {
            _view.Filter = MatchesSearch;
        }

        UpdateCount();
    }

    public void SetItems(
        IEnumerable<MainWindow.GameRowViewModel> games,
        Func<Guid, LibraryGamePreferences> preferencesResolver)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(preferencesResolver);

        _items.Clear();
        foreach (var game in games)
        {
            var preferences = preferencesResolver(game.GameId);
            _items.Add(new OrganizerItem(
                game.GameId,
                game.Title,
                game.Initial,
                game.Icon,
                game.LastActivityText,
                game.AchievementText,
                game.TotalText,
                preferences.IsFavorite,
                preferences.IsHidden,
                preferences.CompletionStatus));
        }

        _view?.Refresh();
        UpdateCount();
    }

    private bool MatchesSearch(object item)
    {
        if (item is not OrganizerItem game)
        {
            return false;
        }

        var query = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        return compare.IndexOf(
            game.Title,
            query,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateCount();
    }

    private void UpdateCount()
    {
        var visible = _view?.Cast<object>().Count() ?? _items.Count;
        CountTextBlock.Text = visible == _items.Count
            ? _items.Count == 1 ? "1 juego" : $"{_items.Count} juegos"
            : $"{visible} de {_items.Count}";
    }

    private void Back_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private async void StatusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox
            {
                DataContext: OrganizerItem game,
                SelectedItem: StatusOption selected
            } combo ||
            selected.Status == game.CompletionStatus ||
            CompletionStatusChangeRequestedAsync is null)
        {
            return;
        }

        combo.IsEnabled = false;
        try
        {
            await CompletionStatusChangeRequestedAsync(game.GameId, selected.Status);
        }
        finally
        {
            combo.IsEnabled = true;
        }
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OrganizerItem game } button ||
            FavoriteToggleRequestedAsync is null)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await FavoriteToggleRequestedAsync(game.GameId);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void Visibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OrganizerItem game } button ||
            VisibilityToggleRequestedAsync is null)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await VisibilityToggleRequestedAsync(game.GameId);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
