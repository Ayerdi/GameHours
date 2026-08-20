namespace GameHours.Core.Domain;

public enum HistoricalSource
{
    Srum = 1,
    UserAssist = 2,
    ManualImport = 3
}

public enum EvidenceKind
{
    Baseline = 1,
    GapRecovery = 2
}

public enum PlaytimeMetric
{
    Foreground = 1,
    Runtime = 2
}

public sealed record HistoricalEvidence
{
    public Guid Id { get; }
    public Guid GameId { get; }
    public HistoricalSource Source { get; }
    public EvidenceKind Kind { get; }
    public PlaytimeMetric Metric { get; }
    public Confidence Confidence { get; }
    public DateTimeOffset PeriodStartUtc { get; }
    public DateTimeOffset PeriodEndUtc { get; }
    public TimeSpan Duration { get; }

    public HistoricalEvidence(
        Guid id,
        Guid gameId,
        HistoricalSource source,
        EvidenceKind kind,
        PlaytimeMetric metric,
        Confidence confidence,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        TimeSpan duration)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Evidence id cannot be empty.", nameof(id));
        }

        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        if (periodEndUtc <= periodStartUtc)
        {
            throw new ArgumentException("Evidence period end must be after its start.", nameof(periodEndUtc));
        }

        if (duration <= TimeSpan.Zero || duration > periodEndUtc - periodStartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Evidence duration must fit inside its covered period.");
        }

        if (confidence is Confidence.Exact)
        {
            throw new ArgumentException("Historical evidence must not claim Exact confidence.", nameof(confidence));
        }

        Id = id;
        GameId = gameId;
        Source = source;
        Kind = kind;
        Metric = metric;
        Confidence = confidence;
        PeriodStartUtc = periodStartUtc.ToUniversalTime();
        PeriodEndUtc = periodEndUtc.ToUniversalTime();
        Duration = duration;
    }
}
