using System.Windows;
using System.Windows.Media;

namespace GameHours.Desktop;

internal static class SessionDetailNavigation
{
    public static bool TryOpenFromVisual(DependencyObject? source, string databasePath, Window? owner)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: MainWindow.ActivityRowViewModel { SessionId: Guid sessionId } })
            {
                new SessionDetailWindow(databasePath, sessionId) { Owner = owner }.ShowDialog();
                return true;
            }

            if (current is FrameworkElement { DataContext: ActivityCalendarView.CalendarEventViewModel { SessionId: Guid calendarSessionId } })
            {
                new SessionDetailWindow(databasePath, calendarSessionId) { Owner = owner }.ShowDialog();
                return true;
            }
        }

        return false;
    }
}
