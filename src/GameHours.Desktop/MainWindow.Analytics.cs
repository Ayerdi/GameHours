using System.Windows;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private ActivityCalendarView? _calendarView;
    private StatisticsView? _statisticsView;

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
        if (_calendarView is not null) await _calendarView.RefreshAsync();
    }

    private async void StatisticsNav_Click(object sender, RoutedEventArgs e)
    {
        ShowAnalyticsView(showCalendar: false);
        if (_statisticsView is not null) await _statisticsView.RefreshAsync();
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

        if (_calendarView is not null) _calendarView.Visibility = showCalendar ? Visibility.Visible : Visibility.Collapsed;
        if (_statisticsView is not null) _statisticsView.Visibility = showCalendar ? Visibility.Collapsed : Visibility.Visible;

        var selected = (WpfBrush)FindResource("SurfaceAltBrush");
        LibraryNavButton.Background = WpfBrushes.Transparent;
        ActivityNavButton.Background = WpfBrushes.Transparent;
        SettingsNavButton.Background = WpfBrushes.Transparent;
        CandidatesNavButton.Background = WpfBrushes.Transparent;
        CalendarNavButton.Background = showCalendar ? selected : WpfBrushes.Transparent;
        StatisticsNavButton.Background = showCalendar ? WpfBrushes.Transparent : selected;
    }

    private void EnsureAnalyticsView(bool calendar)
    {
        if (calendar && _calendarView is null)
        {
            _calendarView = new ActivityCalendarView(_host.DatabasePath);
            MainContentGrid.Children.Add(_calendarView);
        }
        else if (!calendar && _statisticsView is null)
        {
            _statisticsView = new StatisticsView(_host.DatabasePath);
            MainContentGrid.Children.Add(_statisticsView);
        }
    }

    private void HideAnalyticsViews()
    {
        if (_calendarView is not null) _calendarView.Visibility = Visibility.Collapsed;
        if (_statisticsView is not null) _statisticsView.Visibility = Visibility.Collapsed;
        CalendarNavButton.Background = WpfBrushes.Transparent;
        StatisticsNavButton.Background = WpfBrushes.Transparent;
    }
}
