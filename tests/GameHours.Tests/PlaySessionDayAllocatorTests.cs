using GameHours.Core.Domain;
using GameHours.Core.Timeline;

namespace GameHours.Tests;

public sealed class PlaySessionDayAllocatorTests
{
    [Fact]
    public void Split_SessionAcrossMidnightAllocatesOnlyOverlapToEachDay()
    {
        var session = new PlaySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-20T23:30:00Z"),
            DateTimeOffset.Parse("2026-08-21T01:30:00Z"),
            CaptureMethod.Reconciliation,
            Confidence.High);

        var segments = PlaySessionDayAllocator.Split(session, TimeZoneInfo.Utc);

        Assert.Equal(2, segments.Count);
        Assert.Equal(new DateOnly(2026, 8, 20), segments[0].LocalDate);
        Assert.Equal(TimeSpan.FromMinutes(30), segments[0].Duration);
        Assert.Equal(new DateOnly(2026, 8, 21), segments[1].LocalDate);
        Assert.Equal(TimeSpan.FromMinutes(90), segments[1].Duration);
        Assert.Equal(session.Duration, TimeSpan.FromTicks(segments.Sum(item => item.Duration.Ticks)));
    }

    [Fact]
    public void Split_UsesSuppliedTimeZoneForCalendarDate()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "GameHours-Test-UTC+02",
            TimeSpan.FromHours(2),
            "GameHours test",
            "GameHours test");
        var session = new PlaySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-20T22:30:00Z"),
            DateTimeOffset.Parse("2026-08-20T23:30:00Z"),
            CaptureMethod.Reconciliation,
            Confidence.High);

        var segment = Assert.Single(PlaySessionDayAllocator.Split(session, zone));

        Assert.Equal(new DateOnly(2026, 8, 21), segment.LocalDate);
        Assert.Equal(TimeSpan.FromHours(1), segment.Duration);
    }
}
