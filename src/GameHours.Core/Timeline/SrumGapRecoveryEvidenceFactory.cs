using GameHours.Core.Discovery;
using GameHours.Core.Domain;

namespace GameHours.Core.Timeline;

/// <summary>
/// Builds estimated post-cutover evidence from one normalized SRUM sample bucket.
/// SRUM timestamps are sample/flush boundaries rather than exact session boundaries, so the
/// evidence covers a conservative hourly window and never claims an Exact confidence level.
/// </summary>
public static class SrumGapRecoveryEvidenceFactory
{
    private const string IdentitySource = "historical-evidence-srum-gap-foreground-v1";
    private static readonly TimeSpan NominalBucketWidth = TimeSpan.FromHours(1);

    public static HistoricalEvidence Create(
        Guid gameId,
        DateTimeOffset recordedAtUtc,
        TimeSpan duration,
        DateTimeOffset trackingStartedAtUtc)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "SRUM gap duration must be positive.");
        }

        var cutover = trackingStartedAtUtc.ToUniversalTime();
        var end = recordedAtUtc.ToUniversalTime();
        if (end <= cutover)
        {
            throw new TimelineConflictException(
                "SRUM gap recovery requires a sample after tracking_started_at.");
        }

        // SRUM Application Resource Usage timestamps are periodic flush/sample boundaries, not
        // exact activity timestamps. Cover at least the nominal hourly bucket. If a provider ever
        // reports more than one hour of FaceTime in one row, enlarge the uncertainty window only
        // enough to contain that duration rather than truncating the evidence.
        var coverageWidth = duration > NominalBucketWidth ? duration : NominalBucketWidth;
        var start = end - coverageWidth;

        if (start < cutover)
        {
            start = cutover;
            if (duration > end - start)
            {
                throw new TimelineConflictException(
                    "SRUM sample crosses tracking_started_at and cannot be represented as post-cutover gap evidence without double counting.");
            }
        }

        var id = DeterministicGameId.Create(
            IdentitySource,
            $"{gameId:D}:{end.UtcTicks}:{duration.Ticks}");

        return new HistoricalEvidence(
            id,
            gameId,
            HistoricalSource.Srum,
            EvidenceKind.GapRecovery,
            PlaytimeMetric.Foreground,
            Confidence.Estimated,
            start,
            end,
            duration);
    }
}
