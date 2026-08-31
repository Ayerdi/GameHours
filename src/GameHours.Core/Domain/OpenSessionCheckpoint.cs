namespace GameHours.Core.Domain;

public sealed record OpenSessionCheckpoint
{
    public Guid SessionId { get; }
    public Guid GameId { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset LastCheckpointAtUtc { get; }
    public CaptureMethod CaptureMethod { get; }

    public OpenSessionCheckpoint(
        Guid sessionId,
        Guid gameId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset lastCheckpointAtUtc,
        CaptureMethod captureMethod)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session id cannot be empty.", nameof(sessionId));
        }

        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        var started = startedAtUtc.ToUniversalTime();
        var checkpoint = lastCheckpointAtUtc.ToUniversalTime();
        if (checkpoint < started)
        {
            throw new ArgumentException("Checkpoint cannot be before session start.", nameof(lastCheckpointAtUtc));
        }

        SessionId = sessionId;
        GameId = gameId;
        StartedAtUtc = started;
        LastCheckpointAtUtc = checkpoint;
        CaptureMethod = captureMethod;
    }
}
