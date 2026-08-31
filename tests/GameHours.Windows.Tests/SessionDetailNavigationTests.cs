using System.Windows;
using System.Windows.Documents;
using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class SessionDetailNavigationTests
{
    [Fact]
    public void RegisteredRow_ResolvesExactSessionIdentity()
    {
        var row = new object();
        var sessionId = Guid.NewGuid();

        SessionDetailNavigation.Register(row, sessionId);

        Assert.True(SessionDetailNavigation.TryResolveSessionId(row, out var resolved));
        Assert.Equal(sessionId, resolved);
    }

    [Fact]
    public void ReRegisteringRow_ReplacesPreviousSessionIdentity()
    {
        var row = new object();
        var firstSessionId = Guid.NewGuid();
        var secondSessionId = Guid.NewGuid();
        SessionDetailNavigation.Register(row, firstSessionId);

        SessionDetailNavigation.Register(row, secondSessionId);

        Assert.True(SessionDetailNavigation.TryResolveSessionId(row, out var resolved));
        Assert.Equal(secondSessionId, resolved);
    }

    [Fact]
    public void RegisteringEmptySession_RemovesPreviousIdentity()
    {
        var row = new object();
        SessionDetailNavigation.Register(row, Guid.NewGuid());

        SessionDetailNavigation.Register(row, Guid.Empty);

        Assert.False(SessionDetailNavigation.TryResolveSessionId(row, out var resolved));
        Assert.Equal(Guid.Empty, resolved);
    }

    [Fact]
    public void CalendarSessionRow_ResolvesItsOwnSessionIdentity()
    {
        var sessionId = Guid.NewGuid();
        var gameId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var item = new DesktopCalendarEvent(
            DateOnly.FromDateTime(occurredAt.Date),
            occurredAt,
            gameId,
            "Calendar game",
            DesktopCalendarEventKind.Session,
            Duration: TimeSpan.FromMinutes(10),
            SessionId: sessionId);
        var row = new ActivityCalendarView.CalendarEventViewModel(item);

        Assert.True(SessionDetailNavigation.TryResolveSessionId(row, out var resolved));
        Assert.Equal(sessionId, resolved);
    }

    [Fact]
    public void CalendarNonSessionRow_DoesNotInventSessionIdentity()
    {
        var gameId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var item = new DesktopCalendarEvent(
            DateOnly.FromDateTime(occurredAt.Date),
            occurredAt,
            gameId,
            "Calendar game",
            DesktopCalendarEventKind.AchievementUnlocked,
            Title: "Achievement");
        var row = new ActivityCalendarView.CalendarEventViewModel(item);

        Assert.False(SessionDetailNavigation.TryResolveSessionId(row, out var resolved));
        Assert.Equal(Guid.Empty, resolved);
    }

    [Fact]
    public void GetParent_UnattachedContentElement_DoesNotUseVisualTreeTraversal()
    {
        var source = new Run("session");

        var parent = SessionDetailNavigation.GetParent(source);

        Assert.Null(parent);
    }

    [Fact]
    public void GetParent_PlainDependencyObject_ReturnsNoParentWithoutThrowing()
    {
        var source = new DependencyObject();

        var parent = SessionDetailNavigation.GetParent(source);

        Assert.Null(parent);
    }
}
