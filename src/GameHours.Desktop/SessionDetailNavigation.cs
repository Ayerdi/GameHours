using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

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

    internal static bool TryResolveSessionId(object? row, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        if (row is ActivityCalendarView.CalendarEventViewModel { SessionId: Guid calendarSessionId } &&
            calendarSessionId != Guid.Empty)
        {
            sessionId = calendarSessionId;
            return true;
        }

        if (row is not null && SessionReferences.TryGetValue(row, out var reference))
        {
            sessionId = reference.SessionId;
            return true;
        }

        return false;
    }

    public static bool TryOpenFromVisual(DependencyObject? source, string databasePath, Window? owner)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is not FrameworkElement { DataContext: { } row } ||
                !TryResolveSessionId(row, out var sessionId))
            {
                continue;
            }

            Open(databasePath, sessionId, owner);
            return true;
        }

        return false;
    }

    internal static DependencyObject? GetParent(DependencyObject current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        if (current is ContentElement content)
        {
            return ContentOperations.GetParent(content) ??
                   (content as FrameworkContentElement)?.Parent;
        }

        return LogicalTreeHelper.GetParent(current);
    }

    public static void Open(string databasePath, Guid sessionId, Window? owner)
    {
        if (sessionId == Guid.Empty) return;
        new SessionDetailWindow(databasePath, sessionId) { Owner = owner }.ShowDialog();
    }
}
