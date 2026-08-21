using GameHours.Core.Domain;

namespace GameHours.Core.Timeline;

public sealed record LocalDaySessionSegment(
    DateOnly LocalDate,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc)
{
    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;
}

/// <summary>
/// Splits one measured UTC session into the local calendar days it overlaps. Day boundaries
/// are resolved through the supplied time zone, so 23/25-hour daylight-saving days do not
/// silently turn into fixed 24-hour buckets.
/// </summary>
public static class PlaySessionDayAllocator
{
    public static IReadOnlyList<LocalDaySessionSegment> Split(
        PlaySession session,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timeZone);

        var localStart = TimeZoneInfo.ConvertTime(session.StartedAtUtc, timeZone);
        var localEnd = TimeZoneInfo.ConvertTime(session.EndedAtUtc, timeZone);
        var firstDate = DateOnly.FromDateTime(localStart.DateTime);
        var lastDate = DateOnly.FromDateTime(localEnd.DateTime);
        var segments = new List<LocalDaySessionSegment>();

        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var dayStartUtc = LocalMidnightToUtc(date, timeZone);
            var dayEndUtc = LocalMidnightToUtc(date.AddDays(1), timeZone);
            var segmentStart = session.StartedAtUtc > dayStartUtc
                ? session.StartedAtUtc
                : dayStartUtc;
            var segmentEnd = session.EndedAtUtc < dayEndUtc
                ? session.EndedAtUtc
                : dayEndUtc;

            if (segmentEnd > segmentStart)
            {
                segments.Add(new LocalDaySessionSegment(date, segmentStart, segmentEnd));
            }
        }

        return segments;
    }

    public static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        var local = DateTime.SpecifyKind(
            date.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);

        // Midnight is valid for modern Windows zones used by GameHours, but a few historical
        // time zones have moved clocks exactly at midnight. Advance to the first valid local
        // instant rather than throwing if a user browses such a date.
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
