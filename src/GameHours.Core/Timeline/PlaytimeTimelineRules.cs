using GameHours.Core.Domain;

namespace GameHours.Core.Timeline;

public static class PlaytimeTimelineRules
{
    public static void ValidateMeasuredSession(
        PlaySession session,
        DateTimeOffset trackingStartedAtUtc)
    {
        var cutover = trackingStartedAtUtc.ToUniversalTime();
        if (session.StartedAtUtc < cutover)
        {
            throw new TimelineConflictException(
                "Measured sessions cannot claim time before tracking_started_at.");
        }
    }

    public static void ValidateAgainstCutover(
        HistoricalEvidence evidence,
        DateTimeOffset trackingStartedAtUtc)
    {
        var cutover = trackingStartedAtUtc.ToUniversalTime();

        switch (evidence.Kind)
        {
            case EvidenceKind.Baseline when evidence.PeriodEndUtc > cutover:
                throw new TimelineConflictException(
                    "Baseline evidence cannot include time after tracking_started_at.");

            case EvidenceKind.GapRecovery when evidence.PeriodStartUtc < cutover:
                throw new TimelineConflictException(
                    "Gap recovery cannot include time before tracking_started_at.");
        }
    }

    public static bool Overlaps(
        DateTimeOffset leftStartUtc,
        DateTimeOffset leftEndUtc,
        DateTimeOffset rightStartUtc,
        DateTimeOffset rightEndUtc)
    {
        return leftStartUtc < rightEndUtc && leftEndUtc > rightStartUtc;
    }
}

public sealed class TimelineConflictException : InvalidOperationException
{
    public TimelineConflictException(string message) : base(message)
    {
    }
}
