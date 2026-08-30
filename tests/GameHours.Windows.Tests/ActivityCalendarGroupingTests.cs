using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class ActivityCalendarGroupingTests
{
    [Fact]
    public void BuildGameSummaries_AggregatesDailyPlaytimeAndKeepsSessionDetails()
    {
        var date = new DateOnly(2026, 8, 30);
        var gothic = Guid.NewGuid();
        var click = Guid.NewGuid();
        var gothicSessionOne = Guid.NewGuid();
        var gothicSessionTwo = Guid.NewGuid();
        var clickSession = Guid.NewGuid();
        var events = new[]
        {
            new DesktopCalendarEvent(
                date,
                new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero),
                gothic,
                "Gothic 1 Remake",
                DesktopCalendarEventKind.Session,
                TimeSpan.FromMinutes(80),
                SessionId: gothicSessionOne),
            new DesktopCalendarEvent(
                date,
                new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
                click,
                "Click the Button",
                DesktopCalendarEventKind.Session,
                TimeSpan.FromMinutes(30),
                SessionId: clickSession),
            new DesktopCalendarEvent(
                date,
                new DateTimeOffset(2026, 8, 30, 15, 0, 0, TimeSpan.Zero),
                gothic,
                "Gothic 1 Remake",
                DesktopCalendarEventKind.Session,
                TimeSpan.FromMinutes(40),
                SessionId: gothicSessionTwo),
            new DesktopCalendarEvent(
                date,
                new DateTimeOffset(2026, 8, 30, 16, 0, 0, TimeSpan.Zero),
                gothic,
                "Gothic 1 Remake",
                DesktopCalendarEventKind.AchievementUnlocked,
                Title: "Primer logro")
        };
        var day = new DesktopCalendarDay(
            date,
            TimeSpan.FromMinutes(150),
            AchievementCount: 1,
            CompletionCount: 0,
            GameCount: 2,
            events);

        var summaries = ActivityCalendarView.BuildGameSummaries(day);

        Assert.Equal(2, summaries.Count);
        var gothicSummary = summaries[0];
        Assert.Equal(gothic, gothicSummary.GameId);
        Assert.Equal("Gothic 1 Remake", gothicSummary.GameTitle);
        Assert.Equal(TimeSpan.FromHours(2), gothicSummary.MeasuredPlaytime);
        Assert.Equal("2 h", gothicSummary.DurationText);
        Assert.Equal("2 sesiones · 1 logro", gothicSummary.SummaryText);
        Assert.Equal(2, gothicSummary.SessionCount);
        Assert.Equal(1, gothicSummary.AchievementCount);
        Assert.Equal(3, gothicSummary.Events.Count);
        Assert.Equal(2, gothicSummary.Events.Count(item => item.HasSessionDetail));

        var clickSummary = summaries[1];
        Assert.Equal(click, clickSummary.GameId);
        Assert.Equal(TimeSpan.FromMinutes(30), clickSummary.MeasuredPlaytime);
        Assert.Equal("30 min", clickSummary.DurationText);
        Assert.Equal("1 sesión", clickSummary.SummaryText);
    }

    [Fact]
    public void BuildGameSummaries_ListsAchievementOnlyGamesWithoutInventingPlaytime()
    {
        var date = new DateOnly(2026, 8, 30);
        var gameId = Guid.NewGuid();
        var day = new DesktopCalendarDay(
            date,
            TimeSpan.Zero,
            AchievementCount: 1,
            CompletionCount: 1,
            GameCount: 1,
            new[]
            {
                new DesktopCalendarEvent(
                    date,
                    new DateTimeOffset(2026, 8, 30, 10, 0, 0, TimeSpan.Zero),
                    gameId,
                    "Example",
                    DesktopCalendarEventKind.AchievementUnlocked,
                    Title: "Logro"),
                new DesktopCalendarEvent(
                    date,
                    new DateTimeOffset(2026, 8, 30, 10, 1, 0, TimeSpan.Zero),
                    gameId,
                    "Example",
                    DesktopCalendarEventKind.AchievementCompleted,
                    Title: "100 % completado")
            });

        var summary = Assert.Single(ActivityCalendarView.BuildGameSummaries(day));

        Assert.Equal(TimeSpan.Zero, summary.MeasuredPlaytime);
        Assert.Equal("Sin tiempo medido", summary.DurationText);
        Assert.Equal("1 logro · ★ 100 %", summary.SummaryText);
    }
}
