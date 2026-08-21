namespace GameHours.Core.Timeline;

public sealed record ActivityStreakSummary(
    int CurrentDays,
    int LongestDays,
    DateOnly? CurrentStart,
    DateOnly? CurrentEnd,
    DateOnly? LongestStart,
    DateOnly? LongestEnd);

/// <summary>
/// Calculates calendar-day play streaks from exact measured activity dates. A current streak
/// remains active when its latest day is today or yesterday, so opening GameHours before
/// playing today does not prematurely report that yesterday's streak has been lost.
/// </summary>
public static class ActivityStreakCalculator
{
    public static ActivityStreakSummary Calculate(
        IEnumerable<DateOnly> activeDates,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(activeDates);

        var dates = activeDates
            .Distinct()
            .OrderBy(date => date)
            .ToArray();
        if (dates.Length == 0)
        {
            return new ActivityStreakSummary(0, 0, null, null, null, null);
        }

        var longestDays = 1;
        var longestStart = dates[0];
        var longestEnd = dates[0];
        var runStart = dates[0];
        var runLength = 1;

        for (var index = 1; index < dates.Length; index++)
        {
            if (dates[index].DayNumber == dates[index - 1].DayNumber + 1)
            {
                runLength++;
            }
            else
            {
                runStart = dates[index];
                runLength = 1;
            }

            if (runLength > longestDays)
            {
                longestDays = runLength;
                longestStart = runStart;
                longestEnd = dates[index];
            }
        }

        var anchor = dates.Contains(today)
            ? today
            : dates.Contains(today.AddDays(-1))
                ? today.AddDays(-1)
                : (DateOnly?)null;

        if (anchor is null)
        {
            return new ActivityStreakSummary(
                0,
                longestDays,
                null,
                null,
                longestStart,
                longestEnd);
        }

        var set = dates.ToHashSet();
        var currentEnd = anchor.Value;
        var currentStart = currentEnd;
        while (set.Contains(currentStart.AddDays(-1)))
        {
            currentStart = currentStart.AddDays(-1);
        }

        var currentDays = currentEnd.DayNumber - currentStart.DayNumber + 1;
        return new ActivityStreakSummary(
            currentDays,
            longestDays,
            currentStart,
            currentEnd,
            longestStart,
            longestEnd);
    }
}
