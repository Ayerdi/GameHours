using GameHours.Core.Domain;

namespace GameHours.Core.Timeline;

public sealed record HistoricalSourceCoverage(
    HistoricalSource Source,
    TimeSpan KnownDuration,
    DateTimeOffset FirstEvidenceAtUtc,
    DateTimeOffset LastEvidenceAtUtc,
    Confidence MinimumConfidence,
    IReadOnlyList<PlaytimeMetric> Metrics);

public sealed record HistoricalCoverageSummary(
    Guid GameId,
    TimeSpan KnownDuration,
    DateTimeOffset FirstKnownActivityAtUtc,
    DateTimeOffset LastKnownActivityAtUtc,
    IReadOnlyList<HistoricalSourceCoverage> Sources)
{
    public TimeSpan EvidenceWindow => LastKnownActivityAtUtc - FirstKnownActivityAtUtc;

    public bool ContainsEstimatedEvidence =>
        Sources.Any(source => source.MinimumConfidence == Confidence.Estimated);
}

/// <summary>
/// Builds a provenance-oriented summary over already-normalized historical evidence.
/// The covered date range is an evidence window, not a claim that the user played
/// continuously or that no older activity exists outside the retained source data.
/// </summary>
public static class HistoricalCoverageSummarizer
{
    public static HistoricalCoverageSummary? Build(
        Guid gameId,
        IEnumerable<HistoricalEvidence> evidence)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        ArgumentNullException.ThrowIfNull(evidence);
        var items = evidence.ToArray();
        if (items.Length == 0)
        {
            return null;
        }

        if (items.Any(item => item.GameId != gameId))
        {
            throw new ArgumentException(
                "Historical evidence contains rows for a different game.",
                nameof(evidence));
        }

        var knownTicks = items.Aggregate(
            0L,
            (total, item) => checked(total + item.Duration.Ticks));

        var sources = items
            .GroupBy(item => item.Source)
            .Select(group =>
            {
                var rows = group.ToArray();
                var sourceTicks = rows.Aggregate(
                    0L,
                    (total, item) => checked(total + item.Duration.Ticks));
                var minimumConfidence = (Confidence)rows.Min(item => (int)item.Confidence);
                var metrics = rows
                    .Select(item => item.Metric)
                    .Distinct()
                    .OrderBy(metric => (int)metric)
                    .ToArray();

                return new HistoricalSourceCoverage(
                    group.Key,
                    TimeSpan.FromTicks(sourceTicks),
                    rows.Min(item => item.PeriodStartUtc),
                    rows.Max(item => item.PeriodEndUtc),
                    minimumConfidence,
                    metrics);
            })
            .OrderBy(source => (int)source.Source)
            .ToArray();

        return new HistoricalCoverageSummary(
            gameId,
            TimeSpan.FromTicks(knownTicks),
            items.Min(item => item.PeriodStartUtc),
            items.Max(item => item.PeriodEndUtc),
            sources);
    }
}
