using GameHours.Core.Timeline;

namespace GameHours.Tests;

public sealed class ActivityStreakCalculatorTests
{
    [Fact]
    public void Calculate_EmptyActivityHasNoStreaks()
    {
        var result = ActivityStreakCalculator.Calculate(
            Array.Empty<DateOnly>(),
            new DateOnly(2026, 8, 21));

        Assert.Equal(0, result.CurrentDays);
        Assert.Equal(0, result.LongestDays);
        Assert.Null(result.CurrentStart);
        Assert.Null(result.LongestStart);
    }

    [Fact]
    public void Calculate_CurrentStreakCanEndYesterday()
    {
        var result = ActivityStreakCalculator.Calculate(
            new[]
            {
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 18),
                new DateOnly(2026, 8, 19),
                new DateOnly(2026, 8, 20)
            },
            new DateOnly(2026, 8, 21));

        Assert.Equal(4, result.CurrentDays);
        Assert.Equal(new DateOnly(2026, 8, 17), result.CurrentStart);
        Assert.Equal(new DateOnly(2026, 8, 20), result.CurrentEnd);
        Assert.Equal(4, result.LongestDays);
    }

    [Fact]
    public void Calculate_CurrentStreakExpiresAfterFullMissedDay()
    {
        var result = ActivityStreakCalculator.Calculate(
            new[]
            {
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 18),
                new DateOnly(2026, 8, 19)
            },
            new DateOnly(2026, 8, 21));

        Assert.Equal(0, result.CurrentDays);
        Assert.Equal(3, result.LongestDays);
        Assert.Null(result.CurrentStart);
        Assert.Equal(new DateOnly(2026, 8, 17), result.LongestStart);
        Assert.Equal(new DateOnly(2026, 8, 19), result.LongestEnd);
    }

    [Fact]
    public void Calculate_LongestStreakIsIndependentFromCurrentStreak()
    {
        var result = ActivityStreakCalculator.Calculate(
            new[]
            {
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 2),
                new DateOnly(2026, 7, 3),
                new DateOnly(2026, 7, 4),
                new DateOnly(2026, 7, 5),
                new DateOnly(2026, 8, 20),
                new DateOnly(2026, 8, 21)
            },
            new DateOnly(2026, 8, 21));

        Assert.Equal(2, result.CurrentDays);
        Assert.Equal(5, result.LongestDays);
        Assert.Equal(new DateOnly(2026, 7, 1), result.LongestStart);
        Assert.Equal(new DateOnly(2026, 7, 5), result.LongestEnd);
    }
}
