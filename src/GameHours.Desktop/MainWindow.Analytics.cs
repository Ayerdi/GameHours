using System.Windows;
using System.Windows.Controls;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private WpfButton? _calendarNavButton;
    private WpfButton? _statisticsNavButton;
    private Grid? _analyticsContentGrid;
    private ActivityCalendarView? _calendarView;
    private StatisticsView? _statisticsView;

    private void InitializeAnalyticsNavigation()
    {
        if (_calendarNavButton is not null ||
            string.IsNullOrWhiteSpace(_host.DatabasePath) ||
            LibraryNavButton.Parent is not StackPanel navigation ||
            LibraryView.Parent is not Grid contentGrid)
        {
            return;
        }

        _analyticsContentGrid = contentGrid;
        _calendarNavButton = CreateAnalyticsNavButton("Calendario", CalendarNav_Click);
        _statisticsNavButton = CreateAnalyticsNavButton("Estadísticas", StatisticsNav_Click);

        var settingsIndex = navigation.Children.IndexOf(SettingsNavButton);
        if (settingsIndex < 0)
        {
            settingsIndex = navigation.Children.Count;
        }

        navigation.Children.Insert(settingsIndex, _calendarNavButton);
        navigation.Children.Insert(settingsIndex + 1, _statisticsNavButton);

        LibraryNavButton.Click += StandardNavigation_Click;
        ActivityNavButton.Click += StandardNavigation_Click;
        SettingsNavButton.Click += StandardNavigation_Click;
    }

    public void ShowCalendarFromTray()
    {
        ShowFromTray();
        ShowAnalyticsView(showCalendar: true);
        _ = _calendarView?.RefreshAsync();
    }

    public void ShowStatisticsFromTray()
    {
        ShowFromTray();
        ShowAnalyticsView(showCalendar: false);
        _ = _statisticsView?.RefreshAsync();
    }

    private async void CalendarNav_Click(object sender, RoutedEventArgs e)
    {
        ShowAnalyticsView(showCalendar: true);
        if (_calendarView is not null)
        {
            await _calendarView.RefreshAsync();
        }
    }

    private async void StatisticsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowAnalyticsView(showCalendar: false);
        if (_statisticsView is not null)
        {
            await _statisticsView.RefreshAsync();
        }
    }

    private void StandardNavigation_Click(object sender, RoutedEventArgs e) => HideAnalyticsViews();

    private void ShowAnalyticsView(bool showCalendar)
    {
        EnsureAnalyticsView(showCalendar);

        _selectedGameId = null;
        SelectedGameDetail = null;
        LibraryView.Visibility = Visibility.Collapsed;
        ActivityView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        GameDetailPanel.Visibility = Visibility.Collapsed;

        if (_calendarView is not null)
        {
            _calendarView.Visibility = showCalendar ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_statisticsView is not null)
        {
            _statisticsView.Visibility = showCalendar ? Visibility.Collapsed : Visibility.Visible;
        }

        var selected = (WpfBrush)FindResource("SurfaceAltBrush");
        LibraryNavButton.Background = WpfBrushes.Transparent;
        ActivityNavButton.Background = WpfBrushes.Transparent;
        SettingsNavButton.Background = WpfBrushes.Transparent;
        if (_calendarNavButton is not null)
        {
            _calendarNavButton.Background = showCalendar ? selected : WpfBrushes.Transparent;
        }

        if (_statisticsNavButton is not null)
        {
            _statisticsNavButton.Background = showCalendar ? WpfBrushes.Transparent : selected;
        }
    }

    private void EnsureAnalyticsView(bool calendar)
    {
        if (_analyticsContentGrid is null)
        {
            return;
        }

        if (calendar && _calendarView is null)
        {
            _calendarView = new ActivityCalendarView(_host.DatabasePath)
            {
                Visibility = Visibility.Visible
            };
            _analyticsContentGrid.Children.Add(_calendarView);
        }
        else if (!calendar && _statisticsView is null)
        {
            _statisticsView = new StatisticsView(_host.DatabasePath)
            {
                Visibility = Visibility.Visible
            };
            _analyticsContentGrid.Children.Add(_statisticsView);
        }
    }

    private void HideAnalyticsViews()
    {
        if (_calendarView is not null)
        {
            _calendarView.Visibility = Visibility.Collapsed;
        }

        if (_statisticsView is not null)
        {
            _statisticsView.Visibility = Visibility.Collapsed;
        }

        if (_calendarNavButton is not null)
        {
            _calendarNavButton.Background = WpfBrushes.Transparent;
        }

        if (_statisticsNavButton is not null)
        {
            _statisticsNavButton.Background = WpfBrushes.Transparent;
        }
    }

    private static WpfButton CreateAnalyticsNavButton(string text, RoutedEventHandler handler)
    {
        var button = new WpfButton
        {
            Content = text,
            Background = WpfBrushes.Transparent,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        button.Click += handler;
        return button;
    }
}
