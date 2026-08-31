namespace GameHours.Core.Domain;

public sealed record PlaySession
{
    public Guid Id { get; }
    public Guid GameId { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset EndedAtUtc { get; }
    public CaptureMethod CaptureMethod { get; }
    public Confidence Confidence { get; }
    public string? EndReason { get; }

    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;

    public PlaySession(
        Guid id,
        Guid gameId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        CaptureMethod captureMethod,
        Confidence confidence,
        string? endReason = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Session id cannot be empty.", nameof(id));
        }

        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        if (endedAtUtc <= startedAtUtc)
        {
            throw new ArgumentException("Session end must be after session start.", nameof(endedAtUtc));
        }

        if (confidence is Confidence.Estimated)
        {
            throw new ArgumentException("Measured sessions cannot use Estimated confidence.", nameof(confidence));
        }

        Id = id;
        GameId = gameId;
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        EndedAtUtc = endedAtUtc.ToUniversalTime();
        CaptureMethod = captureMethod;
        Confidence = confidence;
        EndReason = string.IsNullOrWhiteSpace(endReason) ? null : endReason.Trim();
    }
}
