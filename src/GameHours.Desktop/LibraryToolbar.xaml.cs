using System.Windows;
using System.Windows.Controls;

namespace GameHours.Desktop;

public enum LibraryFilterScope
{
    All = 0,
    Favorites = 1,
    Running = 2,
    Backlog = 3,
    Playing = 4,
    Completed = 5,
    Abandoned = 6,
    Hidden = 7,
    Paused = 8
}

public partial class LibraryToolbar : UserControl
{
    private sealed record FilterOption(LibraryFilterScope Scope, string Label);

    public event Action? FilterChanged;

    public string SearchText => SearchBox.Text;

    public LibraryFilterScope Scope =>
        ScopeComboBox.SelectedItem is FilterOption option
            ? option.Scope
            : LibraryFilterScope.All;

    public LibraryToolbar()
    {
        InitializeComponent();
        ScopeComboBox.ItemsSource = new[]
        {
            new FilterOption(LibraryFilterScope.All, "Todos"),
            new FilterOption(LibraryFilterScope.Favorites, "Favoritos"),
            new FilterOption(LibraryFilterScope.Running, "En ejecución"),
            new FilterOption(LibraryFilterScope.Backlog, "Pendientes"),
            new FilterOption(LibraryFilterScope.Playing, "Jugando"),
            new FilterOption(LibraryFilterScope.Paused, "Pausados"),
            new FilterOption(LibraryFilterScope.Completed, "Completados"),
            new FilterOption(LibraryFilterScope.Abandoned, "Abandonados"),
            new FilterOption(LibraryFilterScope.Hidden, "Ocultos")
        };
        ScopeComboBox.SelectedIndex = 0;
    }

    public void SetCount(int visibleCount, int totalCount)
    {
        CountTextBlock.Text = visibleCount == totalCount
            ? totalCount == 1 ? "1 juego" : $"{totalCount} juegos"
            : $"{visibleCount} de {totalCount}";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        FilterChanged?.Invoke();

    private void ScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FilterChanged?.Invoke();
}
