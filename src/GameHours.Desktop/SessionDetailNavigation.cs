using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace GameHours.Desktop;

internal static class SessionDetailNavigation
{
    private sealed class SessionReference(Guid sessionId)
    {
        public Guid SessionId { get; } = sessionId;
    }

    private static readonly ConditionalWeakTable<object, SessionReference> SessionReferences = new();

    public static void Register(object row, Guid? sessionId)
    {
        ArgumentNullException.ThrowIfNull(row);
        SessionReferences.Remove(row);
        if (sessionId is Guid id && id != Guid.Empty)
        {
            SessionReferences.Add(row, new SessionReference(id));
        }
    }

    public static bool TryOpenFromVisual(DependencyObject? source, string databasePath, Window? owner)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement { DataContext: { } row }) continue;

            if (row is ActivityCalendarView.CalendarEventViewModel { SessionId: Guid calendarSessionId })
            {
                Open(databasePath, calendarSessionId, owner);
                return true;
            }

            if (SessionReferences.TryGetValue(row, out var reference))
            {
                Open(databasePath, reference.SessionId, owner);
                return true;
            }
        }

        return false;
    }

    public static void Open(string databasePath, Guid sessionId, Window? owner)
    {
        if (sessionId == Guid.Empty) return;
        new SessionDetailWindow(databasePath, sessionId) { Owner = owner }.ShowDialog();
    }
}
