using GameHours.Core.Discovery;
using GameHours.Core.Domain;

namespace GameHours.Core.Timeline;

public static class SrumBaselineEvidenceFactory
{
    private const string IdentitySource = "historical-evidence-srum-baseline-foreground-v1";

    public static HistoricalEvidence Create(
        Guid gameId,
        DateTimeOffset firstObservedAtUtc,
        DateTimeOffset lastObservedAtUtc,
        TimeSpan duration,
        DateTimeOffset trackingStartedAtUtc)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "SRUM baseline duration must be positive.");
        }

        var cutover = trackingStartedAtUtc.ToUniversalTime();
        var first = firstObservedAtUtc.ToUniversalTime();
        var last = lastObservedAtUtc.ToUniversalTime();

        if (first > cutover)
        {
            throw new TimelineConflictException(
                "SRUM baseline cannot start after tracking_started_at.");
        }

        var end = last > cutover ? cutover : last;
        if (end <= first)
        {
            // A single SRUM sample can contain a positive FaceTime value. In that case the
            // row timestamp is only a sample boundary, so use the immutable cutover as the
            // conservative outer bound instead of inventing a measured session boundary.
            end = cutover;
        }

        if (end <= first)
        {
            throw new TimelineConflictException(
                "SRUM baseline does not have a positive pre-cutover coverage interval.");
        }

        // FaceTime belongs to sampled intervals and can slightly exceed the distance between
        // the first and last row timestamps. Extend the evidence coverage backwards only as
        // much as required to contain the aggregate duration; never extend it past cutover.
        var start = first;
        var minimumStart = end - duration;
        if (start > minimumStart)
        {
            start = minimumStart;
        }

        var id = DeterministicGameId.Create(
            IdentitySource,
            $"{gameId:D}:{cutover.UtcTicks}");

        return new HistoricalEvidence(
            id,
            gameId,
            HistoricalSource.Srum,
            EvidenceKind.Baseline,
            PlaytimeMetric.Foreground,
            Confidence.Estimated,
            start,
            end,
            duration);
    }
}
